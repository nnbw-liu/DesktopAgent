using System;
using Xunit;

namespace CommandLine.Tests.Unit.AgentEval
{
    public class T01Tests
    {
        [Fact]
        public void MaximumDisplayWidth_rejects_non_positive_values()
        {
            var settings = new ParserSettings();

            Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaximumDisplayWidth = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaximumDisplayWidth = -10);
            Assert.Equal(80, settings.MaximumDisplayWidth = 80);
        }
    }
}
