// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CSharpRepl.Services;
using CSharpRepl.Services.Dotnet;
using CSharpRepl.Services.Roslyn;
using CSharpRepl.Services.Roslyn.Scripting;
using Xunit;

namespace CSharpRepl.Tests;

[Collection(nameof(RoslynServices))]
public class EvaluationTests : IAsyncLifetime
{
    private readonly RoslynServices services;
    private readonly StringBuilder stdout;
    private readonly IConsoleEx console;

    public EvaluationTests()
    {
        (console, stdout) = FakeConsole.CreateStubbedOutput();
        this.services = new RoslynServices(console, new Configuration(), new TestTraceLogger());
    }

    public Task InitializeAsync() => services.WarmUpAsync([]);
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Evaluate_LiteralInteger_ReturnsInteger()
    {
        var result = await services.EvaluateAsync("5");

        var success = Assert.IsType<EvaluationResult.Success>(result);
        Assert.Equal("5", success.Input);

        var returnValue = Assert.IsType<int>(success.ReturnValue.Value);
        Assert.Equal(5, returnValue);
    }

    [Fact]
    public async Task Evaluate_Variable_ReturnsValue()
    {
        var variableAssignment = await services.EvaluateAsync(@"var x = ""Hello World"";");
        var variableUsage = await services.EvaluateAsync(@"x.Replace(""World"", ""Mundo"")");

        var assignment = Assert.IsType<EvaluationResult.Success>(variableAssignment);
        var usage = Assert.IsType<EvaluationResult.Success>(variableUsage);
        Assert.Null(assignment.ReturnValue.Value);
        Assert.Equal("Hello Mundo", usage.ReturnValue);
    }

    [Fact]
    public async Task Evaluate_NugetPackage_InstallsPackage()
    {
        var installation = await services.EvaluateAsync(@"#r ""nuget:Newtonsoft.Json""");
        var usage = await services.EvaluateAsync(@"Newtonsoft.Json.JsonConvert.SerializeObject(new { Foo = ""bar"" })");

        var installationResult = Assert.IsType<EvaluationResult.Success>(installation);
        var usageResult = Assert.IsType<EvaluationResult.Success>(usage);

        Assert.Null(installationResult.ReturnValue.Value);
        Assert.Contains(installationResult.References, r => r.Display.EndsWith("Newtonsoft.Json.dll"));
        Assert.Contains("Adding references for 'Newtonsoft.Json", stdout.ToString().RemoveFormatting());
        Assert.Equal(@"{""Foo"":""bar""}", usageResult.ReturnValue);
    }

    [Fact]
    public async Task Evaluate_NugetPackageVersioned_InstallsPackageVersion()
    {
        var installation = await services.EvaluateAsync(@"#r ""nuget:Microsoft.CodeAnalysis.CSharp, 3.11.0""");
        var usage = await services.EvaluateAsync(@"Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(""5"")");

        var installationResult = Assert.IsType<EvaluationResult.Success>(installation);
        var usageResult = Assert.IsType<EvaluationResult.Success>(usage);

        Assert.Null(installationResult.ReturnValue.Value);
        Assert.NotNull(usageResult.ReturnValue.Value);
        Assert.Contains("Adding references for 'Microsoft.CodeAnalysis.CSharp.3.11.0'", stdout.ToString().RemoveFormatting());
    }

    [Fact]
    public async Task Evaluate_RelativeAssemblyReference_CanReferenceAssembly()
    {
        var referenceResult = await services.EvaluateAsync(@"#r ""./Data/DemoLibrary.dll""");
        var importResult = await services.EvaluateAsync("using DemoLibrary;");
        var multiplyResult = await services.EvaluateAsync("DemoClass.Multiply(5, 6)");

        Assert.IsType<EvaluationResult.Success>(referenceResult);
        Assert.IsType<EvaluationResult.Success>(importResult);
        var successfulResult = Assert.IsType<EvaluationResult.Success>(multiplyResult);
        Assert.Equal(30, successfulResult.ReturnValue);
    }

    [Fact]
    public async Task Evaluate_AbsoluteAssemblyReference_CanReferenceAssembly()
    {
        var absolutePath = Path.GetFullPath("./Data/DemoLibrary.dll");
        var referenceResult = await services.EvaluateAsync(@$"#r ""{absolutePath}""");
        var importResult = await services.EvaluateAsync("using DemoLibrary;");
        var multiplyResult = await services.EvaluateAsync("DemoClass.Multiply(7, 6)");

        Assert.IsType<EvaluationResult.Success>(referenceResult);
        Assert.IsType<EvaluationResult.Success>(importResult);
        var successfulResult = Assert.IsType<EvaluationResult.Success>(multiplyResult);
        Assert.Equal(42, successfulResult.ReturnValue);
    }

    [Fact]
    public async Task Evaluate_AssemblyReferenceInSearchPath_CanReferenceAssembly()
    {
        var referenceResult = await services.EvaluateAsync(@"#r ""System.Linq.dll""");

        Assert.IsType<EvaluationResult.Success>(referenceResult);
    }

    [Fact]
    public async Task Evaluate_AssemblyReferenceWithSharedFramework_ReferencesSharedFramework()
    {
        var referenceResult = await services.EvaluateAsync(@"#r ""./Data/WebApplication1.dll""");
        var sharedFrameworkResult = await services.EvaluateAsync(@"using Microsoft.AspNetCore.Hosting;");
        var applicationResult = await services.EvaluateAsync(@"using WebApplication1;");

        Assert.IsType<EvaluationResult.Success>(referenceResult);
        Assert.IsType<EvaluationResult.Success>(sharedFrameworkResult);
        Assert.IsType<EvaluationResult.Success>(applicationResult);

        var completions = await services.CompleteAsync(@"using WebApplicat", 17);
        Assert.Contains("WebApplication1", completions.Select(c => c.Item.DisplayText).First(text => text.StartsWith("WebApplicat")));
    }

    [Fact]
    public async Task Evaluate_ProjectReference_ReferencesProject()
    {
        var referenceResult = await services.EvaluateAsync(@"#r ""./../../../../CSharpRepl.Services/CSharpRepl.Services.csproj""");
        var importResult = await services.EvaluateAsync(@"using CSharpRepl.Services;");

        Assert.IsType<EvaluationResult.Success>(referenceResult);
        Assert.IsType<EvaluationResult.Success>(importResult);
    }

    [Fact]
    public async Task Evaluate_SolutionReference_ReferencesAllProjects()
    {
        var referenceResult = await services.EvaluateAsync(@"#r ""./Data/DemoSolution/DemoSolution.sln""");
        var importProject1Result = await services.EvaluateAsync(@"using DemoSolution.DemoProject1;");
        var importProject2Result = await services.EvaluateAsync(@"using DemoSolution.DemoProject2;");

        Assert.IsType<EvaluationResult.Success>(referenceResult);
        Assert.IsType<EvaluationResult.Success>(importProject1Result);
        Assert.IsType<EvaluationResult.Success>(importProject2Result);
    }

    [Fact]
    public async Task Evaluate_SolutionReference_ReferencesMultipleTargetFrameworks()
    {
        var referenceResult = await services.EvaluateAsync(@"#r ""./Data/ComplexSolution/ComplexSolution.sln""");
        var importEntryPoint = await services.EvaluateAsync(@"using EntryPoint;");
        var importLibraryA = await services.EvaluateAsync(@"using LibraryA;");
        var importLibraryB = await services.EvaluateAsync(@"using LibraryB;");
        var callResult = await services.EvaluateAsync(@"Program.Main();");
        // we should be able to import the nuget package dependency from LibraryB.
        var importNugetPackage = await services.EvaluateAsync(@"using Newtonsoft.Json;");

        Assert.IsType<EvaluationResult.Success>(referenceResult);
        Assert.IsType<EvaluationResult.Success>(importEntryPoint);
        Assert.IsType<EvaluationResult.Success>(importLibraryA);
        Assert.IsType<EvaluationResult.Success>(importLibraryB);
        Assert.IsType<EvaluationResult.Success>(callResult);
        Assert.IsType<EvaluationResult.Success>(importNugetPackage);
    }

    /// <summary>
    /// https://github.com/waf/CSharpRepl/issues/128
    /// </summary>
    [Fact]
    public async Task Evaluate_ResolveCorrectRuntimeVersionOfReferencedAssembly()
    {
        var builder = new DotnetBuilder(console);
        var (buildExitCode, _) = builder.Build("./Data/DemoSolution/DemoSolution.DemoProject3");
        Assert.Equal(0, buildExitCode);

        var referenceResult = await services.EvaluateAsync(@"#r ""./Data/DemoSolution/DemoSolution.DemoProject3/bin/Debug/net6.0/DemoSolution.DemoProject3.dll""");
        var importResult = await services.EvaluateAsync(@"DemoSolution.DemoProject3.DemoClass3.GetSystemManagementPath()");

        Assert.IsType<EvaluationResult.Success>(referenceResult);
        Assert.IsType<EvaluationResult.Success>(importResult);

        var referencedSystemManagementPath = (string)((EvaluationResult.Success)importResult).ReturnValue.Value;
        referencedSystemManagementPath = referencedSystemManagementPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var winRuntimeSelected = referencedSystemManagementPath.Contains(Path.Combine("runtimes", "win", "lib"), StringComparison.OrdinalIgnoreCase);
        var isWin = Environment.OSVersion.Platform == PlatformID.Win32NT;
        Assert.Equal(isWin, winRuntimeSelected);
    }

    /// <summary>
    /// https://github.com/waf/CSharpRepl/issues/318
    /// </summary>
    [Fact]
    public async Task Evaluate_SpanResult()
    {

        var eval1 = await services.EvaluateAsync(@"new[]{1,2,3}.AsSpan()");
        dynamic r1 = Assert.IsType<EvaluationResult.Success>(eval1).ReturnValue.Value;
        Assert.EndsWith($"{nameof(__CSharpRepl_RuntimeHelper)}+{nameof(__CSharpRepl_RuntimeHelper.SpanOutput)}", r1.GetType().FullName);
        Assert.Equal(3, r1.Count);
        Assert.Equal(typeof(Span<int>), r1.OriginalType);

        var eval2 = await services.EvaluateAsync(@"(ReadOnlySpan<int>)[1,2,3]");
        dynamic r2 = Assert.IsType<EvaluationResult.Success>(eval2).ReturnValue.Value;
        Assert.EndsWith($"{nameof(__CSharpRepl_RuntimeHelper)}+{nameof(__CSharpRepl_RuntimeHelper.SpanOutput)}", r2.GetType().FullName);
        Assert.Equal(3, r2.Count);
        Assert.Equal(typeof(ReadOnlySpan<int>), r2.OriginalType);

        var eval3 = await services.EvaluateAsync(@"""Hello World"".AsSpan()");
        dynamic r3 = Assert.IsType<EvaluationResult.Success>(eval3).ReturnValue.Value;
        Assert.EndsWith($"{nameof(__CSharpRepl_RuntimeHelper)}+{nameof(__CSharpRepl_RuntimeHelper.CharSpanOutput)}", r3.GetType().FullName);
        Assert.Equal(11, r3.Count);
        Assert.Equal(typeof(ReadOnlySpan<char>), r3.OriginalType);
    }

    /// <summary>
    /// https://github.com/waf/CSharpRepl/issues/317
    /// </summary>
    [Fact]
    public async Task Evaluate_MemoryResult()
    {

        var eval1 = await services.EvaluateAsync(@"new[]{1,2,3}.AsMemory()");
        dynamic r1 = Assert.IsType<EvaluationResult.Success>(eval1).ReturnValue.Value;
        Assert.EndsWith($"{nameof(__CSharpRepl_RuntimeHelper)}+{nameof(__CSharpRepl_RuntimeHelper.SpanOutput)}", r1.GetType().FullName);
        Assert.Equal(3, r1.Count);
        Assert.Equal(typeof(Memory<int>), r1.OriginalType);

        var eval2 = await services.EvaluateAsync(@"(ReadOnlyMemory<int>)new[] { 1, 2, 3 }.AsMemory()");
        dynamic r2 = Assert.IsType<EvaluationResult.Success>(eval2).ReturnValue.Value;
        Assert.EndsWith($"{nameof(__CSharpRepl_RuntimeHelper)}+{nameof(__CSharpRepl_RuntimeHelper.SpanOutput)}", r2.GetType().FullName);
        Assert.Equal(3, r2.Count);
        Assert.Equal(typeof(ReadOnlyMemory<int>), r2.OriginalType);

        var eval3 = await services.EvaluateAsync(@"""Hello World"".AsMemory()");
        dynamic r3 = Assert.IsType<EvaluationResult.Success>(eval3).ReturnValue.Value;
        Assert.EndsWith($"{nameof(__CSharpRepl_RuntimeHelper)}+{nameof(__CSharpRepl_RuntimeHelper.CharSpanOutput)}", r3.GetType().FullName);
        Assert.Equal(11, r3.Count);
        Assert.Equal(typeof(ReadOnlyMemory<char>), r3.OriginalType);
    }

    /// <summary>
    /// https://github.com/waf/CSharpRepl/issues/318
    /// </summary>
    [Fact]
    public async Task Evaluate_RefStructResult()
    {
        var e1 = await services.EvaluateAsync(@"ref struct S; default(S)");
        var r1 = Assert.IsType<EvaluationResult.Success>(e1).ReturnValue.Value;
        Assert.EndsWith($"{nameof(__CSharpRepl_RuntimeHelper)}+{nameof(__CSharpRepl_RuntimeHelper.RefStructOutput)}", r1.GetType().FullName);
        Assert.Equal($"Cannot output a value of 'S' because it's a ref-struct. It has to override ToString() to see its value.", r1.ToString());

        var e2 = await services.EvaluateAsync(@"ref struct S{public override string ToString()=>""custom result"";} default(S)");
        var r2 = Assert.IsType<EvaluationResult.Success>(e2).ReturnValue.Value;
        Assert.EndsWith($"{nameof(__CSharpRepl_RuntimeHelper)}+{nameof(__CSharpRepl_RuntimeHelper.RefStructOutput)}", r2.GetType().FullName);
        Assert.Equal("custom result", r2.ToString());
    }
}
