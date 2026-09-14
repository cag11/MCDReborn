using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

//Matches MCDSaveEdit: these tests drive WPF-referencing code, so the assembly is
//Windows-only too. Without it CA1416 flags every call into the app.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]


[assembly: AssemblyTitle("MCDSaveEditTests")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("MCDSaveEditTests")]
[assembly: AssemblyCopyright("Copyright ©  2020")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: Guid("45b35b4e-59cb-4ef1-ace1-ad646e7901e1")]

// [assembly: AssemblyVersion("1.0.*")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
