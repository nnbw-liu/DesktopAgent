using System;
using CommandLine.Text;
using Xunit;

namespace CommandLine.Tests.Unit.AgentEval
{
    public class T03Tests
    {
        [Fact]
        public void HelpText_width_rejects_non_positive_values()
        {
            var help = new HelpText();

            Assert.Throws<ArgumentOutOfRangeException>(() => help.MaximumDisplayWidth = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => help.MaximumDisplayWidth = -10);
            help.MaximumDisplayWidth = 40;
            Assert.Equal(40, help.MaximumDisplayWidth);
        }
    }
}
