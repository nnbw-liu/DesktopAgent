using CommandLine.Tests.Fakes;
using Xunit;

namespace CommandLine.Tests.Unit.AgentEval
{
    public class T08Tests
    {
        [Fact]
        public void WithSkipDefaultOnly_sets_only_skip_default()
        {
            var settings = UnParserSettings.WithSkipDefaultOnly();

            Assert.True(settings.SkipDefault);
            Assert.False(settings.PreferShortName);
            Assert.False(settings.GroupSwitches);
            Assert.False(settings.UseEqualToken);
            Assert.False(settings.ShowHidden);
        }

        [Fact]
        public void SkipDefault_factory_omits_default_values()
        {
            var options = new Options_With_Defaults
            {
                P1 = 99,
                P2 = "xyz",
                P3 = 88,
                P4 = Shapes.Square
            };

            var command = new Parser().FormatCommandLine(options, config =>
            {
                var factory = UnParserSettings.WithSkipDefaultOnly();
                config.SkipDefault = factory.SkipDefault;
            });

            Assert.Equal("--p2 xyz", command);
        }
    }
}
