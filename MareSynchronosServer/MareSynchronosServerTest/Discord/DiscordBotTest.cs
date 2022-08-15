using FluentAssertions;
using MareSynchronosServer.Discord;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace MareSynchronosServerTest.Discord {
    public class DiscordBotTest {

        [Test]
        [TestCase("", null)]
        [TestCase("abcd", null)]
        [TestCase("www.google.de", null)]
        [TestCase("https://www.google.de", null)]
        [TestCase("de.finalfantasyxiv.com/lodestone/character/1234", null)]
        [TestCase("https://cn.finalfantasyxiv.com/lodestone/character/1234", null)]
        [TestCase("http://jp.finalfantasyxiv.com/lodestone/character/1234", null)]
        [TestCase("https://jp.finalfantasyxiv.com/character/1234", null)]
        [TestCase("https://jp.finalfantasyxiv.com/lodestone/1234", null)]
        [TestCase("https://www.finalfantasyxiv.com/lodestone/character/1234", null)]
        [TestCase("https://jp.finalfantasyxiv.com/lodestone/character/1234", 1234)]
        [TestCase("https://fr.finalfantasyxiv.com/lodestone/character/1234", 1234)]
        [TestCase("https://eu.finalfantasyxiv.com/lodestone/character/1234/", 1234)]
        [TestCase("https://eu.finalfantasyxiv.com/lodestone/character/1234?myurlparameter=500", 1234)]
        [TestCase("https://de.finalfantasyxiv.com/lodestone/character/1234/whatever/3456", 1234)]
        [TestCase("https://na.finalfantasyxiv.com/lodestone/character/1234abcd4321/whatever/3456", 1234)]
        public void ParseCharacterIdFromLodestoneUrl_CheckThatIdIsParsedCorrectly(string url, int? expectedId) {
            var inMemorySettings = new Dictionary<string, string> {
                {"DiscordBotToken", "1234"}
            };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            var spMock = new Mock<IServiceProvider>();
            var loggerMock = new Mock<ILogger<DiscordBot>>();

            var sut = new DiscordBot(spMock.Object, configuration, loggerMock.Object);
            MethodInfo methodInfo = sut.GetType().GetMethod("ParseCharacterIdFromLodestoneUrl", BindingFlags.NonPublic | BindingFlags.Instance);
            var actualId = methodInfo.Invoke(sut, new object[] { url });

            actualId.Should().Be(expectedId);
        }
    }
}
