using System.Collections.Generic;
using Avalonia;
using ScreenShot_One_Avalonia;
using Xunit;

namespace ScreenShot_One_Avalonia.Tests
{
    /// <summary>Тести функцій 2.10.0: знімок вікна, фон і рамка.</summary>
    public class FeatureTests2100
    {
        [Fact]
        public void WindowHitTest_PicksTopmostWindowUnderCursor()
        {
            var windows = new List<PixelRect>
            {
                new PixelRect(100, 100, 400, 300),   // верхнє
                new PixelRect(0, 0, 1920, 1080),     // під ним (на весь екран)
            };
            Assert.Equal(windows[0], WindowLocator.HitTest(windows, new PixelPoint(150, 150)));
            Assert.Equal(windows[1], WindowLocator.HitTest(windows, new PixelPoint(50, 50)));
            Assert.Null(WindowLocator.HitTest(windows, new PixelPoint(3000, 50)));
            Assert.Null(WindowLocator.HitTest(new List<PixelRect>(), new PixelPoint(1, 1)));
        }

        [Fact]
        public void Frame_AddsPaddingOnEverySide()
        {
            Assert.Equal(new PixelSize(1296, 796), FrameStyle.OutputSize(new PixelSize(1200, 700), 48));
            Assert.Equal(new PixelSize(1200, 700), FrameStyle.OutputSize(new PixelSize(1200, 700), 0));
            Assert.Equal(7, FrameStyle.Presets.Length);
        }
    }
}
