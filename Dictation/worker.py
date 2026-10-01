"""Private stdio worker. Downloads only in prepare mode; inference is offline."""
import argparse
import base64
import contextlib
import io
import json
import os
import shutil
from pathlib import Path
import sys
import wave

# Revisions fix the exact weights used by a release. Never execute model repo code.
SPEECH = {
    "base": ("Systran/faster-whisper-base", "ebe41f70d5b6dfa9166e2c581c45c9c0cfc57b66"),
    "small": ("Systran/faster-whisper-small", "536b0662742c02347bc0e980a01041f333bce120"),
    "medium": ("Systran/faster-whisper-medium", "08e178d48790749d25932bbc082711ddcfdfbc4f"),
    "large-v3": ("Systran/faster-whisper-large-v3", "edaa852ec7e145841d8ffdb056a99866b5f0a478"),
}
SPEECH_TORCH = {
    "base": ("openai/whisper-base", "e37978b90ca9030d5170a5c07aadb050351a65bb"),
    "small": ("openai/whisper-small", "973afd24965f72e36ca33b3055d56a652f456b4d"),
    "medium": ("openai/whisper-medium", "abdf7c39ab9d0397620ccaea8974cc764cd0953e"),
    "large-v3": ("openai/whisper-large-v3", "06f233fe06e710322aca913c1bc4249a0d71fce1"),
}
SPEECH_MLX = {
    "base": ("mlx-community/whisper-base-mlx", "1e3e249fb8d01c655324bd6841b1deadffd6d04c"),
    "small": ("mlx-community/whisper-small-mlx", "45f3915923c7a79a5a5b5a7d909d39aeb0e5630e"),
    "medium": ("mlx-community/whisper-medium-mlx", "7fc08c4eac4c316526498f147dfdee6f6303f975"),
    "large-v3": ("mlx-community/whisper-large-v3-mlx", "49e6aa286ad60c14352c404340ded53710378a11"),
}
DIALOGUE = {
    "1.7B": ("Qwen/Qwen3-1.7B", "70d244cc86ccca08cf5af4e1e306ecf908b1ad5e"),
    "4B": ("Qwen/Qwen3-4B", "1cfa9a7208912126459214e8b04321603b3df60c"),
}

# Keep short replies, but reject isolated clicks and trim long non-speech gaps.
# The packaged Silero model is local; VAD never downloads during inference.
VAD_OPTIONS = dict(threshold=0.6, min_speech_duration_ms=120,
                   min_silence_duration_ms=300, speech_pad_ms=250)


def filter_speech(samples):
    import numpy as np
    from faster_whisper.vad import get_speech_timestamps, VadOptions
    if not len(samples):
        return samples
    spans = get_speech_timestamps(samples, VadOptions(**VAD_OPTIONS))
    return np.concatenate([samples[span["start"]:span["end"]] for span in spans]) if spans else samples[:0]


def model_path(root, model):
    return root / "models" / (model[0].replace("/", "--") + "-" + model[1])


def prepare(root, speech, dialogue, repair=False, backend="cpu"):
    # Validate accelerator availability before downloading model weights.
    engine = create_engine(root, speech, dialogue, backend)
    from huggingface_hub import snapshot_download
    for model in (speech, dialogue):
        print("Downloading " + model[0], flush=True)
        snapshot_download(repo_id=model[0], revision=model[1],
                          local_dir=str(model_path(root, model)), token=False, force_download=repair,
                          allow_patterns=["*.json", "*.safetensors", "weights.npz", "model.bin",
                                          "vocabulary.*", "merges.txt", "*.jinja", "LICENSE*"],
                          ignore_patterns=["*.fp32*", "original/*", "onnx/*", "coreml/*"])
    # Convert the pinned dialogue weights once to a CPU-efficient INT8 format.
    # A cancelled conversion is retried, never treated as a working model.
    dialogue_path = model_path(root, dialogue)
    converted = dialogue_path / ("mlx-int8" if backend == "mlx" else "ct2-int8")
    conversion_marker = dialogue_path / ("converted-mlx-0.28.3" if backend == "mlx" else "converted-ct2-4.8.2")
    weights = converted / ("model.safetensors" if backend == "mlx" else "model.bin")
    if backend in ("cpu", "mlx") and (repair or not conversion_marker.exists() or not weights.exists()):
        print("Preparing dialogue model for this computer", flush=True)
        if backend == "mlx":
            from mlx_lm.convert import convert
            # Only the derived copy is disposable. A cancelled conversion must
            # be repeatable, and mlx-lm refuses an existing destination.
            if converted.is_symlink() or converted.resolve().parent != dialogue_path.resolve():
                raise ValueError("Invalid conversion directory")
            if converted.exists():
                shutil.rmtree(converted)
            convert(hf_path=str(dialogue_path), mlx_path=str(converted), quantize=True,
                    q_bits=8, dtype="float16", trust_remote_code=False)
        else:
            import ctranslate2
            ctranslate2.converters.TransformersConverter(str(dialogue_path), load_as_float16=True,
                trust_remote_code=False).convert(str(converted), quantization="int8", force=True)
        conversion_marker.write_text("ready", encoding="utf-8")
    # Import and load both once before declaring setup complete. Missing wheels,
    # incompatible CPUs or incomplete downloads must fail during setup.
    print("Checking speech model", flush=True)
    engine.load_speech()
    # Exercise the decoder and VAD too: loading weights alone would miss an
    # incompatible audio dependency and report readiness until the first clip.
    silence = io.BytesIO()
    with wave.open(silence, "wb") as audio:
        audio.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
        audio.writeframes(b"\0" * 32000)
    engine.run({"operation": "transcribe", "language": "en",
                "audio": base64.b64encode(silence.getvalue()).decode("ascii")})
    print("Checking dialogue model", flush=True)
    engine.load_dialogue()
    print("Ready", flush=True)


class Engine:
    def __init__(self, root, speech, dialogue):
        self.speech_path = model_path(root, speech)
        self.dialogue_path = model_path(root, dialogue)
        self.speech = self.dialogue = self.tokenizer = None

    def load_speech(self):
        if self.speech is None:
            from faster_whisper import WhisperModel
            self.speech = WhisperModel(str(self.speech_path), device="cpu", compute_type="int8",
                                       cpu_threads=min(8, os.cpu_count() or 4), local_files_only=True)

    def load_dialogue(self):
        if self.dialogue is None:
            import ctranslate2
            from transformers import AutoTokenizer
            self.tokenizer = AutoTokenizer.from_pretrained(str(self.dialogue_path),
                                                           local_files_only=True, trust_remote_code=False)
            self.dialogue = ctranslate2.Generator(str(self.dialogue_path / "ct2-int8"),
                device="cpu", compute_type="int8", intra_threads=min(8, os.cpu_count() or 4))

    def run(self, request):
        if request["operation"] == "warmup":
            # Load both before the first audio clip. GPU adapters retain the
            # inactive model in RAM; leave speech in VRAM for the first clip.
            self.load_dialogue()
            self.load_speech()
            return ""
        if request.get("language") not in ("en", "de"):
            raise ValueError("Unsupported language")
        if request["operation"] == "transcribe":
            audio = base64.b64decode(request["audio"], validate=True)
            if len(audio) > 8 * 1024 * 1024:
                raise ValueError("Audio too large")
            return self.transcribe(audio, request["language"])
        if request["operation"] != "format":
            raise ValueError("Unsupported operation")
        self.load_dialogue()
        messages = [
            {"role": "system", "content": request["instruction"]},
            {"role": "user", "content": json.dumps({
                "language": request["language"], "precedingText": request["precedingText"],
                "transcript": request["transcript"]}, ensure_ascii=False)},
        ]
        return self.format(messages, request["transcript"])

    def transcribe(self, audio, language):
        self.load_speech()
        segments, _ = self.speech.transcribe(io.BytesIO(audio), language=language,
            task="transcribe", beam_size=5, vad_filter=True, vad_parameters=VAD_OPTIONS,
            temperature=0, no_speech_threshold=0.6, log_prob_threshold=-1.0,
            condition_on_previous_text=False)
        return " ".join(segment.text.strip() for segment in segments).strip()

    def format(self, messages, transcript):
        text = self.tokenizer.apply_chat_template(messages, tokenize=False,
                                                  add_generation_prompt=True, enable_thinking=False)
        tokens = self.tokenizer.convert_ids_to_tokens(self.tokenizer.encode(text, add_special_tokens=False))
        if len(tokens) > 10000:
            raise ValueError("Transcript too long")
        prefix = self.tokenizer.apply_chat_template(messages[:1], tokenize=False, add_generation_prompt=False)
        prefix_tokens = self.tokenizer.convert_ids_to_tokens(self.tokenizer.encode(prefix, add_special_tokens=False))
        static = prefix_tokens if tokens[:len(prefix_tokens)] == prefix_tokens else []
        generated = []
        completed = None

        def stop_at_json(step):
            nonlocal completed
            generated.append(step.token_id)
            if "}" not in step.token:
                return False
            candidate = complete_json(self.tokenizer.decode(generated, skip_special_tokens=True))
            if candidate is not None:
                completed = candidate
                return True
            return False

        result = self.dialogue.generate_batch([tokens[len(static):]],
            static_prompt=static or None, cache_static_prompt=True, include_prompt_in_result=False,
            max_length=min(2048, len(transcript) * 3 + 128), sampling_topk=1,
            end_token=[self.tokenizer.eos_token], callback=stop_at_json)[0]
        return completed or self.tokenizer.decode(result.sequences_ids[0], skip_special_tokens=True)


def complete_json(text):
    text = text.strip()
    if text.startswith("```") and "\n" in text:
        text = text.split("\n", 1)[1]
    try:
        # One generated token can contain the root's closing brace plus extra
        # punctuation or commentary. Stop at the complete object within it.
        value, end = json.JSONDecoder().raw_decode(text)
        return text[:end] if isinstance(value, dict) else None
    except ValueError:
        return None


def create_engine(root, speech, dialogue, backend):
    if backend == "cpu":
        return Engine(root, speech, dialogue)
    # GPU libraries are imported only in the selected private environment.
    from accelerated import MlxEngine, TorchEngine
    return MlxEngine(root, speech, dialogue) if backend == "mlx" else TorchEngine(root, speech, dialogue, backend)


def serve(engine, incoming, outgoing):
    for line in incoming:
        try:
            # Libraries may print notices. Only protocol JSON belongs on stdout.
            with contextlib.redirect_stdout(sys.stderr):
                result = engine.run(json.loads(line))
            reply = {"result": result}
        except Exception as error:
            # Neither logs nor UI errors should contain dictated text/audio.
            reply = {"error": type(error).__name__}
        outgoing.write(json.dumps(reply, ensure_ascii=False) + "\n")
        outgoing.flush()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["prepare", "serve"])
    parser.add_argument("root", type=Path)
    parser.add_argument("speech", choices=SPEECH)
    parser.add_argument("dialogue", choices=DIALOGUE)
    parser.add_argument("--repair", action="store_true")
    parser.add_argument("--backend", choices=("cpu", "cuda", "rocm", "mlx"), default="cpu")
    args = parser.parse_args()
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
    os.environ["HF_HOME"] = str(args.root / "hub")
    models = SPEECH_MLX if args.backend == "mlx" else SPEECH_TORCH if args.backend in ("cuda", "rocm") else SPEECH
    if args.mode == "prepare":
        prepare(args.root, models[args.speech], DIALOGUE[args.dialogue], args.repair, args.backend)
    else:
        # Even a missing/corrupt model must fail locally, never fall back to Hub.
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        serve(create_engine(args.root, models[args.speech], DIALOGUE[args.dialogue], args.backend), sys.stdin, sys.stdout)


if __name__ == "__main__":
    main()
