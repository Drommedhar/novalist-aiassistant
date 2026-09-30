using Novalist.Extensions.AiAssistant.Services;
using Xunit;

public class DictationHardwareTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090", "cuda")]
    [InlineData("AMD Radeon RX 9070 XT", "rocm")]
    [InlineData("AMD Radeon(TM) RX 7900 XTX", "rocm")]
    [InlineData("AMD Radeon RX 7800 XT", "cpu")]
    [InlineData("AMD Radeon RX 9070 GRE", "cpu")]
    [InlineData("Intel Arc A770", "cpu")]
    [InlineData("Parsec Virtual Display Adapter", "cpu")]
    public void AutomaticWindowsSelectionRespectsSupportedGpuFamilies(string adapter, string expected)
        => Assert.Equal(expected, DictationHardware.SelectWindows([adapter]));

    [Fact]
    public void VirtualAdaptersDoNotHideTheSupportedGpu()
        => Assert.Equal("rocm", DictationHardware.SelectWindows(["Parsec Virtual Display Adapter", "AMD Radeon RX 9070 XT"]));

    [Fact]
    public void CpuCanBeSelectedEvenWhenAGpuWasDetected()
        => Assert.Equal("cpu", DictationHardware.Resolve("cpu"));

    [Theory]
    [InlineData("../../cuda")]
    [InlineData("http://localhost")]
    public void InvalidChoicesCannotBecomePathsOrServices(string choice)
        => Assert.Throws<ArgumentException>(() => DictationHardware.Resolve(choice));
}
