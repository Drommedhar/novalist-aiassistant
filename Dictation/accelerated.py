"""Offline GPU adapters. CUDA and ROCm use PyTorch; Apple Silicon uses MLX."""
import io
import wave

from worker import Engine, complete_json, filter_speech


def waveform(audio):
    import numpy as np
    with wave.open(io.BytesIO(audio), "rb") as wav:
        if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) != (1, 2, 16000):
            raise ValueError("Expected mono 16 kHz PCM16 WAV")
        return np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2").astype(np.float32) / 32768.0


class TorchEngine(Engine):
    def __init__(self, root, speech, dialogue, backend):
        super().__init__(root, speech, dialogue)
        import torch
        if backend not in ("cuda", "rocm"):
            raise ValueError("Unknown GPU backend")
        if not torch.cuda.is_available() or not (torch.version.hip if backend == "rocm" else torch.version.cuda):
            raise RuntimeError("Selected GPU unavailable; check the driver or select CPU in dictation settings")
        # Do actual device work. A listed adapter alone does not prove that the
        # installed wheel supports its architecture or that the driver works.
        probe = torch.ones((2, 2), device="cuda", dtype=torch.float16)
        if not torch.isfinite(probe @ probe).all().item():
            raise RuntimeError("GPU probe failed")
        torch.cuda.synchronize()
        self.torch = torch
        self.processor = None

    def load_speech(self):
        from transformers import WhisperForConditionalGeneration, WhisperProcessor
        # Only one model occupies VRAM at a time, including when Large v3 and
        # Qwen3 4B are selected together. The inactive model stays in host RAM.
        if self.dialogue is not None:
            self.dialogue.to("cpu")
        self.torch.cuda.empty_cache()
        if self.speech is None:
            self.processor = WhisperProcessor.from_pretrained(str(self.speech_path), local_files_only=True)
            self.speech = WhisperForConditionalGeneration.from_pretrained(str(self.speech_path),
                local_files_only=True, trust_remote_code=False, torch_dtype=self.torch.float16,
                attn_implementation="sdpa").eval()
        self.speech.to("cuda")

    def load_dialogue(self):
        from transformers import AutoModelForCausalLM, AutoTokenizer
        if self.speech is not None:
            self.speech.to("cpu")
            self.torch.cuda.empty_cache()
        if self.dialogue is None:
            self.tokenizer = AutoTokenizer.from_pretrained(str(self.dialogue_path),
                local_files_only=True, trust_remote_code=False)
            self.dialogue = AutoModelForCausalLM.from_pretrained(str(self.dialogue_path),
                local_files_only=True, trust_remote_code=False, torch_dtype=self.torch.float16,
                attn_implementation="sdpa").eval()
        self.dialogue.to("cuda")

    def transcribe(self, audio, language, vocabulary=""):
        samples = filter_speech(waveform(audio))
        if not len(samples):
            return ""
        self.load_speech()
        inputs = self.processor(samples, sampling_rate=16000, return_tensors="pt",
            return_attention_mask=True, truncation=False, padding="longest")
        prompt = self.processor.get_prompt_ids(vocabulary, return_tensors="pt")[:224].to("cuda") if vocabulary else None
        with self.torch.inference_mode():
            result = self.speech.generate(
                inputs.input_features.to("cuda", dtype=self.torch.float16),
                attention_mask=inputs.attention_mask.to("cuda"),
                language=language, task="transcribe", do_sample=False, num_beams=5,
                return_timestamps=len(samples) > 30 * 16000, condition_on_prev_tokens=False, prompt_ids=prompt)
        return self.processor.batch_decode(result, skip_special_tokens=True)[0].strip()

    def format(self, messages, transcript):
        from transformers import StoppingCriteria, StoppingCriteriaList
        text = self.tokenizer.apply_chat_template(messages, tokenize=False,
            add_generation_prompt=True, enable_thinking=False)
        inputs = self.tokenizer(text, return_tensors="pt", add_special_tokens=False).to("cuda")
        length = inputs.input_ids.shape[-1]
        if length > 10000:
            raise ValueError("Transcript too long")
        tokenizer = self.tokenizer

        class JsonStop(StoppingCriteria):
            def __call__(self, input_ids, scores, **kwargs):
                return complete_json(tokenizer.decode(input_ids[0, length:], skip_special_tokens=True)) is not None

        with self.torch.inference_mode():
            result = self.dialogue.generate(**inputs, max_new_tokens=min(2048, len(transcript) * 3 + 128),
                do_sample=False, pad_token_id=self.tokenizer.eos_token_id,
                stopping_criteria=StoppingCriteriaList([JsonStop()]))
        decoded = self.tokenizer.decode(result[0, length:], skip_special_tokens=True)
        return complete_json(decoded) or decoded


class MlxEngine(Engine):
    def __init__(self, root, speech, dialogue):
        super().__init__(root, speech, dialogue)
        import platform
        import mlx.core as mx
        if platform.system() != "Darwin" or platform.machine() != "arm64" or not mx.metal.is_available():
            raise RuntimeError("MLX dictation requires native Apple Silicon Python and Metal")
        mx.set_default_device(mx.gpu)
        mx.eval(mx.ones((2, 2)) @ mx.ones((2, 2)))

    def load_speech(self):
        import mlx.core as mx
        from mlx_whisper.transcribe import ModelHolder
        self.speech = ModelHolder.get_model(str(self.speech_path), mx.float16)

    def load_dialogue(self):
        if self.dialogue is None:
            from mlx_lm import load
            self.dialogue, self.tokenizer = load(str(self.dialogue_path / "mlx-int8"),
                tokenizer_config={"local_files_only": True, "trust_remote_code": False})

    def transcribe(self, audio, language, vocabulary=""):
        import mlx_whisper
        samples = filter_speech(waveform(audio))
        if not len(samples):
            return ""
        # Supplying samples avoids mlx-whisper's external ffmpeg executable.
        result = mlx_whisper.transcribe(samples, path_or_hf_repo=str(self.speech_path),
            language=language, task="transcribe", temperature=0, no_speech_threshold=0.6,
            logprob_threshold=-1.0, condition_on_previous_text=False, verbose=None, initial_prompt=vocabulary or None)
        return result["text"].strip()

    def format(self, messages, transcript):
        from mlx_lm import stream_generate
        from mlx_lm.sample_utils import make_sampler
        text = self.tokenizer.apply_chat_template(messages, tokenize=False,
            add_generation_prompt=True, enable_thinking=False)
        tokens = self.tokenizer.encode(text, add_special_tokens=False)
        if len(tokens) > 10000:
            raise ValueError("Transcript too long")
        result = ""
        stream = stream_generate(self.dialogue, self.tokenizer, prompt=tokens,
            max_tokens=min(2048, len(transcript) * 3 + 128), sampler=make_sampler(temp=0))
        try:
            for response in stream:
                result += response.text
                complete = complete_json(result)
                if complete is not None:
                    return complete
        finally:
            stream.close()
        return result
