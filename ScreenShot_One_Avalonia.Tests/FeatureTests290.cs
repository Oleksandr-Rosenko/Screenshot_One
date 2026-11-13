using ScreenShot_One_Avalonia;
using Xunit;

namespace ScreenShot_One_Avalonia.Tests
{
    /// <summary>Тести функцій 2.9.0: показ натиснутих клавіш.</summary>
    public class FeatureTests290
    {
        [Fact]
        public void Keystrokes_Windows_CombosAndTyping()
        {
            Assert.Equal("Ctrl + C", KeystrokeFormatter.FromWindows(0x43, HotkeyModifiers.Ctrl)!.Value.Text);
            Assert.Equal("Ctrl + Shift + S", KeystrokeFormatter.FromWindows(0x53, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift)!.Value.Text);
            var a = KeystrokeFormatter.FromWindows(0x41, 0)!.Value;
            Assert.True(a.IsTyping);
            Assert.Equal("a", a.Text);
            Assert.Equal("A", KeystrokeFormatter.FromWindows(0x41, HotkeyModifiers.Shift)!.Value.Text);
            Assert.Equal("Enter", KeystrokeFormatter.FromWindows(0x0D, 0)!.Value.Text);
            Assert.Equal("Alt + F4", KeystrokeFormatter.FromWindows(0x73, HotkeyModifiers.Alt)!.Value.Text);
            Assert.Null(KeystrokeFormatter.FromWindows(0x11, HotkeyModifiers.Ctrl)); // сам Ctrl — нічого
        }

        [Fact]
        public void Gif_UsesOwnPalette_AndLimitsWidth()
        {
            var args = GifExport.BuildArguments("in.mp4", "out.gif");
            string vf = args[System.Array.IndexOf(args, "-vf") + 1];
            Assert.Contains("palettegen", vf);
            Assert.Contains("paletteuse", vf);
            Assert.Contains("fps=15", vf);
            Assert.Contains("min(960,iw)", vf);
            Assert.Equal("out.gif", args[^1]);
            Assert.Contains("-loop", args);
        }

        [Fact]
        public void Keystrokes_Mac_UsesSymbols()
        {
            Assert.Equal("⌘C", KeystrokeFormatter.FromMac(8, HotkeyModifiers.Meta)!.Value.Text);
            Assert.Equal("⇧⌘S", KeystrokeFormatter.FromMac(1, HotkeyModifiers.Meta | HotkeyModifiers.Shift)!.Value.Text);
            Assert.Equal("Return", KeystrokeFormatter.FromMac(36, 0)!.Value.Text);
            Assert.True(KeystrokeFormatter.FromMac(0, 0)!.Value.IsTyping);
        }
    }
}
