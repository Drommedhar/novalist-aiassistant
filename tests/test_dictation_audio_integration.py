"""Offline checks using the installed CPU runtime, without microphone access.

Set NOVALIST_DICTATION_INTEGRATION=1. Run with the prepared runtime's Python.
"""
import os
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).parents[1] / "Dictation"))


@unittest.skipUnless(os.environ.get("NOVALIST_DICTATION_INTEGRATION") == "1", "Requires prepared local VAD")
class AudioIntegrationTests(unittest.TestCase):
    def test_silence_hum_noise_and_keyboard_clicks_are_not_speech(self):
        import numpy as np
        from worker import filter_speech
        random = np.random.default_rng(42)
        count = 3 * 16000
        clicks = np.zeros(count, dtype=np.float32)
        for start in range(3000, count - 100, 3200):
            clicks[start:start + 80] = random.uniform(-0.4, 0.4, 80)
        for label, samples in (
            ("silence", np.zeros(count, dtype=np.float32)),
            ("hum", (0.03 * np.sin(2 * np.pi * 100 * np.arange(count) / 16000)).astype(np.float32)),
            ("noise", random.normal(0, 0.03, count).astype(np.float32)),
            ("clicks", clicks),
        ):
            with self.subTest(audio=label):
                self.assertEqual(len(filter_speech(samples)), 0)

    def test_spoken_fixtures_keep_speech_at_normal_and_quiet_volume(self):
        import wave
        import numpy as np
        from worker import filter_speech
        root = os.environ.get("NOVALIST_DICTATION_FIXTURES")
        if not root:
            self.skipTest("Set NOVALIST_DICTATION_FIXTURES to English/German WAVs")
        for language in ("en", "de"):
            with wave.open(str(Path(root) / (language + ".wav")), "rb") as source:
                self.assertEqual((source.getnchannels(), source.getsampwidth(), source.getframerate()), (1, 2, 16000))
                samples = np.frombuffer(source.readframes(source.getnframes()), dtype="<i2").astype(np.float32) / 32768
            for volume in (1, 0.1):
                with self.subTest(language=language, volume=volume):
                    self.assertGreater(len(filter_speech(samples * volume)), 16000)


if __name__ == "__main__":
    unittest.main()
