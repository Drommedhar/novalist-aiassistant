"""Protocol tests without downloads or third-party dependencies.

Run: python -m unittest discover -s tests -p test_dictation_worker.py
"""
import base64
import io
from itertools import product
import json
from pathlib import Path
import types
import unittest
import sys
from unittest.mock import patch, Mock, MagicMock

sys.path.insert(0, str(Path(__file__).parents[1] / "Dictation"))
import worker
import accelerated


class WorkerTests(unittest.TestCase):
    def test_warmup_loads_both_models_without_audio_and_leaves_speech_active(self):
        for engine_type in (worker.Engine, accelerated.TorchEngine, accelerated.MlxEngine):
            engine = object.__new__(engine_type)
            calls = []
            engine.load_dialogue = lambda: calls.append("dialogue")
            engine.load_speech = lambda: calls.append("speech")
            self.assertEqual(engine.run({"operation": "warmup"}), "")
            self.assertEqual(calls, ["dialogue", "speech"])

    def test_generation_stops_at_a_complete_object_even_with_extra_closing_text_in_the_token(self):
        expected = '{"segments":[{"text":"Hallo.","kind":"dialogue"}]}'
        for suffix in ("", "}", "\n```", " Here is an explanation."):
            self.assertEqual(worker.complete_json(expected + suffix), expected)
        self.assertEqual(worker.complete_json("```json\n" + expected + "}\n```"), expected)
        self.assertIsNone(worker.complete_json(expected[:-1]))
        self.assertIsNone(worker.complete_json("Explanation " + expected))

    def test_each_language_is_transcribed_locally_without_translation(self):
        for speech_model, language in product(("small", "large-v3"), ("en", "de")):
            with self.subTest(model=speech_model, language=language):
                model = worker.SPEECH[speech_model]
                engine = worker.Engine(Path("root"), model, worker.DIALOGUE["1.7B"])
                speech = Mock()
                speech.transcribe.return_value = ([types.SimpleNamespace(text=" Hallo "), types.SimpleNamespace(text="Welt.")], None)
                module = types.SimpleNamespace(WhisperModel=Mock(return_value=speech))
                with patch.dict("sys.modules", {"faster_whisper": module}):
                    result = engine.run({"operation": "transcribe", "language": language,
                                         "audio": base64.b64encode(b"audio").decode()})
                self.assertEqual(result, "Hallo Welt.")
                self.assertEqual(module.WhisperModel.call_args.args[0], str(worker.model_path(Path("root"), model)))
                self.assertTrue(module.WhisperModel.call_args.kwargs["local_files_only"])
                self.assertEqual(module.WhisperModel.call_args.kwargs["device"], "cpu")
                options = speech.transcribe.call_args.kwargs
                self.assertEqual(options["language"], language)
                self.assertEqual(options["task"], "transcribe")
                self.assertTrue(options["vad_filter"])
                self.assertEqual(speech.transcribe.call_args.args[0].read(), b"audio")

    def test_protocol_recovers_after_errors_and_keeps_diagnostics_off_stdout(self):
        def run(request):
            print("library notice")
            if request["bad"]:
                raise ValueError("private dictated text")
            return "Wörtliche Rede."
        outgoing = io.StringIO()
        errors = io.StringIO()
        with patch("sys.stderr", errors):
            worker.serve(types.SimpleNamespace(run=run), io.StringIO('{"bad":true}\n{"bad":false}\n'), outgoing)
        replies = [json.loads(line) for line in outgoing.getvalue().splitlines()]
        self.assertEqual(replies, [{"error": "ValueError"}, {"result": "Wörtliche Rede."}])
        self.assertNotIn("private", outgoing.getvalue() + errors.getvalue())

    def test_serve_mode_forces_offline_even_if_inherited_environment_allows_network(self):
        with patch.dict(worker.os.environ, {"HF_HUB_OFFLINE": "0", "TRANSFORMERS_OFFLINE": "0"}), \
             patch("sys.argv", ["worker.py", "serve", "root", "small", "1.7B"]), \
             patch.object(worker, "serve") as serve:
            worker.main()
            self.assertEqual(worker.os.environ["HF_HUB_OFFLINE"], "1")
            self.assertEqual(worker.os.environ["TRANSFORMERS_OFFLINE"], "1")
            self.assertEqual(worker.os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"], "1")
            serve.assert_called_once()

    def test_unsupported_language_fails_before_any_model_is_loaded(self):
        engine = worker.Engine(Path("root"), worker.SPEECH["small"], worker.DIALOGUE["1.7B"])
        with self.assertRaises(ValueError):
            engine.run({"operation": "transcribe", "language": "fr"})
        self.assertIsNone(engine.speech)

    def test_gpu_transcription_uses_the_selected_runtime_and_keeps_language(self):
        for backend, language in product(("cuda", "rocm"), ("en", "de")):
            with self.subTest(backend=backend, language=language):
                torch = MagicMock()
                torch.version.hip = "7.2" if backend == "rocm" else None
                torch.version.cuda = "12.8" if backend == "cuda" else None
                torch.cuda.is_available.return_value = True
                torch.isfinite.return_value.all.return_value.item.return_value = True
                speech = MagicMock()
                speech.eval.return_value = speech
                processor = MagicMock()
                processor.batch_decode.return_value = ["Dictated words."]
                transformers = types.SimpleNamespace(
                    WhisperForConditionalGeneration=Mock(from_pretrained=Mock(return_value=speech)),
                    WhisperProcessor=Mock(from_pretrained=Mock(return_value=processor)))
                with patch.dict(sys.modules, {"torch": torch, "transformers": transformers}), \
                     patch.object(accelerated, "waveform", return_value=[0.5]), \
                     patch.object(accelerated, "filter_speech", side_effect=lambda samples: samples):
                    engine = accelerated.TorchEngine(Path("root"), worker.SPEECH_TORCH["large-v3"], worker.DIALOGUE["4B"], backend)
                    engine.dialogue = Mock()
                    result = engine.transcribe(b"audio", language)
                self.assertEqual(result, "Dictated words.")
                speech.to.assert_called_with("cuda")  # PyTorch uses this name for ROCm too.
                engine.dialogue.to.assert_called_with("cpu")
                self.assertTrue(transformers.WhisperForConditionalGeneration.from_pretrained.call_args.kwargs["local_files_only"])
                self.assertEqual(speech.generate.call_args.kwargs["language"], language)
                self.assertEqual(speech.generate.call_args.kwargs["task"], "transcribe")

    def test_wrong_gpu_wheel_or_missing_driver_fails_instead_of_using_cpu_silently(self):
        for available, cuda, hip, backend in [(False, "12.8", None, "cuda"),
                (True, None, "7.2", "cuda"), (True, "12.8", None, "rocm")]:
            torch = MagicMock()
            torch.cuda.is_available.return_value = available
            torch.version.cuda = cuda
            torch.version.hip = hip
            with patch.dict(sys.modules, {"torch": torch}), self.assertRaises(RuntimeError):
                accelerated.TorchEngine(Path("root"), worker.SPEECH_TORCH["small"], worker.DIALOGUE["4B"], backend)
            torch.ones.assert_not_called()

    def test_mlx_uses_local_models_and_stops_when_dialogue_json_is_complete(self):
        mx = MagicMock()
        mlx = types.SimpleNamespace(core=mx)
        speech = types.SimpleNamespace(transcribe=Mock(return_value={"text": "Hallo."}))
        tokenizer = Mock()
        tokenizer.encode.return_value = [1, 2]
        closed = []

        def stream(*args, **kwargs):
            try:
                yield types.SimpleNamespace(text='{"segments":[')
                yield types.SimpleNamespace(text='{"text":"Hallo.","kind":"dialogue"}]}')
                raise AssertionError("Must stop before extra model commentary")
            finally:
                closed.append(True)

        lm = types.SimpleNamespace(load=Mock(return_value=(object(), tokenizer)), stream_generate=Mock(side_effect=stream))
        with patch.dict(sys.modules, {"mlx": mlx, "mlx.core": mx, "mlx_whisper": speech,
                "mlx_lm": lm, "mlx_lm.sample_utils": types.SimpleNamespace(make_sampler=Mock())}), \
             patch("platform.system", return_value="Darwin"), patch("platform.machine", return_value="arm64"), \
             patch.object(accelerated, "waveform", return_value=[0.5]), \
             patch.object(accelerated, "filter_speech", side_effect=lambda samples: samples):
            engine = accelerated.MlxEngine(Path("root"), worker.SPEECH_MLX["small"], worker.DIALOGUE["4B"])
            self.assertEqual(engine.transcribe(b"audio", "de"), "Hallo.")
            result = engine.run({"operation": "format", "language": "de", "instruction": "format",
                                 "transcript": "Hallo.", "precedingText": ""})
        self.assertEqual(json.loads(result)["segments"][0]["text"], "Hallo.")
        self.assertEqual(closed, [True])
        mx.set_default_device.assert_called_once_with(mx.gpu)
        self.assertEqual(speech.transcribe.call_args.kwargs["language"], "de")
        self.assertEqual(speech.transcribe.call_args.kwargs["path_or_hf_repo"], str(engine.speech_path))
        self.assertEqual(lm.load.call_args.kwargs["tokenizer_config"], {"local_files_only": True, "trust_remote_code": False})

    def test_mlx_refuses_rosetta_python(self):
        mx = MagicMock()
        with patch.dict(sys.modules, {"mlx": types.SimpleNamespace(core=mx), "mlx.core": mx}), \
             patch("platform.system", return_value="Darwin"), patch("platform.machine", return_value="x86_64"), \
             self.assertRaises(RuntimeError):
            accelerated.MlxEngine(Path("root"), worker.SPEECH_MLX["small"], worker.DIALOGUE["4B"])
        mx.eval.assert_not_called()

    def test_non_speech_never_reaches_gpu_or_mlx_recognition(self):
        for engine_type in (accelerated.TorchEngine, accelerated.MlxEngine):
            with self.subTest(engine=engine_type.__name__), \
                 patch.dict(sys.modules, {"mlx_whisper": Mock()}), \
                 patch.object(accelerated, "waveform", return_value=[0.1] * 16000), \
                 patch.object(accelerated, "filter_speech", return_value=[]) as gate:
                engine = object.__new__(engine_type)
                engine.load_speech = Mock(side_effect=AssertionError("Noise must not load recognition"))
                self.assertEqual(engine.transcribe(b"noise", "en"), "")
                gate.assert_called_once()
                engine.load_speech.assert_not_called()

    def test_vad_removes_noise_and_keeps_all_detected_speech_with_padding(self):
        samples = list(range(100))
        spans = [{"start": 10, "end": 30}, {"start": 60, "end": 90}]
        vad = types.SimpleNamespace(get_speech_timestamps=Mock(return_value=spans), VadOptions=Mock())
        numpy = types.SimpleNamespace(concatenate=lambda chunks: [sample for chunk in chunks for sample in chunk])
        with patch.dict(sys.modules, {"numpy": numpy, "faster_whisper": types.ModuleType("faster_whisper"), "faster_whisper.vad": vad}):
            self.assertEqual(worker.filter_speech(samples), samples[10:30] + samples[60:90])
            vad.get_speech_timestamps.return_value = []
            self.assertEqual(worker.filter_speech(samples), [])
            self.assertEqual(worker.filter_speech([]), [])
        self.assertEqual(vad.VadOptions.call_args.kwargs["min_speech_duration_ms"], 120)
        self.assertEqual(vad.VadOptions.call_args.kwargs["speech_pad_ms"], 250)


if __name__ == "__main__":
    unittest.main()
