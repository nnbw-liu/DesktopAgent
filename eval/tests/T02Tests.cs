using CommandLine.Tests.Fakes;
using Xunit;

namespace CommandLine.Tests.Unit.AgentEval
{
    public class T02Tests
    {
        [Fact]
        public void IsParsed_reflects_parse_outcome()
        {
            var parser = new Parser();

            var parsed = parser.ParseArguments<Simple_Options>(new[] { "--stringvalue", "x" });
            var failed = parser.ParseArguments<Simple_Options>(new[] { "--not-a-real-option" });

            Assert.True(parsed.IsParsed);
            Assert.False(failed.IsParsed);
        }
    }
}
