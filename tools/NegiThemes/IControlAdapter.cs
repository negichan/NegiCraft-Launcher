using System.Xml.Linq;

namespace NegiThemes;

internal interface IControlAdapter
{
    IEnumerable<XElement> Compile(CompileSession session);
}
