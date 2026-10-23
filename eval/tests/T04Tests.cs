using CommandLine.Tests.Fakes;
using Xunit;

namespace CommandLine.Tests.Unit.AgentEval
{
    public class T04Tests
    {
        [Fact]
        public void Escaped_quotes_survive_format_then_split()
        {
            var options = new Simple_Options { StringValue = "say \"hi\" now" };
            var command = new Parser().FormatCommandLine(options);

            var args = command.SplitArgs();

            Assert.Equal(2, args.Length);
            Assert.Equal("say \"hi\" now", args[1]);
        }
    }
}
