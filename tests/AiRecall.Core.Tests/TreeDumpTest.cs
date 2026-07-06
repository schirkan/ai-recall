using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using Xunit;
using Xunit.Abstractions;

namespace AiRecall.Core.Tests.Trigger;

public class TreeDumpTest
{
    private readonly ITestOutputHelper _out;
    public TreeDumpTest(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void DumpTree()
    {
        var sections = ConfigSchemaReflection.GetTopLevelSections(new AppConfig());
        Dump(sections, 0);
    }

    private void Dump(IReadOnlyList<ConfigSectionDescriptor> sections, int indent)
    {
        var pad = new string(' ', indent * 2);
        foreach (var s in sections)
        {
            _out.WriteLine($"{pad}{s.Name} (path={s.Path}, props={s.Properties.Count})");
            if (s.SubSections.Count > 0) Dump(s.SubSections, indent + 1);
        }
    }
}
