using ScreenShot_One_Avalonia;
using Xunit;

namespace ScreenShot_One_Avalonia.Tests
{
    /// <summary>Тести 3.3.0: підтримати проєкт.</summary>
    public class FeatureTests330
    {
        [Fact]
        public void Donate_PicksLinkByLanguage()
        {
            const string mono = "https://send.monobank.ua/jar/x", kofi = "https://ko-fi.com/x";
            Assert.Equal(mono, Donate.Pick("uk", mono, kofi));
            Assert.Equal(kofi, Donate.Pick("en", mono, kofi));
            Assert.Equal(kofi, Donate.Pick("ru", mono, kofi));
            // Немає потрібного — беремо інше; немає жодного — кнопки приховані
            Assert.Equal(mono, Donate.Pick("en", mono, ""));
            Assert.Equal(kofi, Donate.Pick("uk", "", kofi));
            Assert.Null(Donate.Pick("uk", "", ""));
        }
    }
}
