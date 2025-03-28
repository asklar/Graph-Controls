#module nuget:?package=Cake.LongPath.Module&version=0.7.0

#addin nuget:?package=Cake.FileHelpers&version=3.3.0
#addin nuget:?package=Cake.Powershell&version=0.4.8

using System;
using System.Linq;
using System.Text.RegularExpressions;

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////

var target = Argument("target", "Default");

//////////////////////////////////////////////////////////////////////
// VERSIONS
//////////////////////////////////////////////////////////////////////

var gitVersioningVersion = "3.3.37";
var inheritDocVersion = "2.5.2";

//////////////////////////////////////////////////////////////////////
// VARIABLES
//////////////////////////////////////////////////////////////////////

var baseDir = MakeAbsolute(Directory("../")).ToString();
var buildDir = baseDir + "/build";
var Solution = baseDir + "/Windows-Toolkit-Graph-Controls.sln";
var toolsDir = buildDir + "/tools";

var binDir = baseDir + "/bin";
var nupkgDir = binDir + "/nupkg";

var styler = toolsDir + "/XamlStyler.Console/tools/xstyler.exe";
var stylerFile = baseDir + "/settings.xamlstyler";

var versionClient = toolsDir + "/nerdbank.gitversioning/tools/Get-Version.ps1";
string Version = null;

var inheritDoc = toolsDir + "/InheritDoc/tools/InheritDoc.exe";

// Ignoring NerdBank until this is merged and we can use a new version of inheridoc:
// https://github.com/firesharkstudios/InheritDoc/pull/27
var inheritDocExclude = "Nerdbank.GitVersioning.ManagedGit.GitRepository";

//////////////////////////////////////////////////////////////////////
// METHODS
//////////////////////////////////////////////////////////////////////

void VerifyHeaders(bool Replace)
{
    var header = FileReadText("header.txt") + "\r\n";
    bool hasMissing = false;

    Func<IFileSystemInfo, bool> exclude_objDir =
        fileSystemInfo => !fileSystemInfo.Path.Segments.Contains("obj");

    var files = GetFiles(baseDir + "/**/*.cs", exclude_objDir).Where(file =>
    {
        var path = file.ToString();
        return !(path.EndsWith(".g.cs") || path.EndsWith(".i.cs") || System.IO.Path.GetFileName(path).Contains("TemporaryGeneratedFile"));
    });

    Information("\nChecking " + files.Count() + " file header(s)");
    foreach(var file in files)
    {
        var oldContent = FileReadText(file);
		if(oldContent.Contains("// <auto-generated>"))
		{
		   continue;
		}
        var rgx = new Regex("^(//.*\r?\n)*\r?\n");
        var newContent = header + rgx.Replace(oldContent, "");

        if(!newContent.Equals(oldContent, StringComparison.Ordinal))
        {
            if(Replace)
            {
                Information("\nUpdating " + file + " header...");
                FileWriteText(file, newContent);
            }
            else
            {
                Error("\nWrong/missing header on " + file);
                hasMissing = true;
            }
        }
    }

    if(!Replace && hasMissing)
    {
        throw new Exception("Please run UpdateHeaders.bat or '.\\build.ps1 -target=UpdateHeaders' and commit the changes.");
    }
}

//////////////////////////////////////////////////////////////////////
// DEFAULT TASK
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Description("Clean the output folder")
    .Does(() =>
{
    if(DirectoryExists(binDir))
    {
        Information("\nCleaning Working Directory");
        CleanDirectory(binDir);
    }
    else
    {
        CreateDirectory(binDir);
    }
});

Task("Verify")
    .Description("Run pre-build verifications")
    .IsDependentOn("Clean")
    .Does(() =>
{
    VerifyHeaders(false);

    StartPowershellFile("./Find-WindowsSDKVersions.ps1");
});

Task("Version")
    .Description("Updates the version information in all Projects")
    .IsDependentOn("Verify")
    .Does(() =>
{
    Information("\nDownloading NerdBank GitVersioning...");
    var installSettings = new NuGetInstallSettings {
        ExcludeVersion  = true,
        Version = gitVersioningVersion,
        OutputDirectory = toolsDir
    };

    NuGetInstall(new []{"nerdbank.gitversioning"}, installSettings);

    Information("\nRetrieving version...");
    var results = StartPowershellFile(versionClient);
    Version = results[1].Properties["NuGetPackageVersion"].Value.ToString();
    Information("\nBuild Version: " + Version);
});

Task("Build")
    .Description("Build all projects and get the assemblies")
    .IsDependentOn("Version")
    .Does(() =>
{
    Information("\nBuilding Solution");
    var buildSettings = new MSBuildSettings
    {
        MaxCpuCount = 0
    }
    .SetConfiguration("CI")
    .WithTarget("Restore");

    MSBuild(Solution, buildSettings);

    EnsureDirectoryExists(nupkgDir);

	// Build once with normal dependency ordering
    buildSettings = new MSBuildSettings
    {
        MaxCpuCount = 0
    }
    .SetConfiguration("CI")
    .WithTarget("Build")
    .WithProperty("GenerateLibraryLayout", "true");

	MSBuild(Solution, buildSettings);
});

Task("InheritDoc")
	.Description("Updates <inheritdoc /> tags from base classes, interfaces, and similar methods")
	.IsDependentOn("Build")
	.Does(() =>
{
	Information("\nDownloading InheritDoc...");
	var installSettings = new NuGetInstallSettings {
		ExcludeVersion = true,
        Version = inheritDocVersion,
		OutputDirectory = toolsDir
	};

	NuGetInstall(new []{"InheritDoc"}, installSettings);
    
    var args = new ProcessArgumentBuilder()
                .AppendSwitchQuoted("-b", baseDir)
                .AppendSwitch("-o", "")
                .AppendSwitchQuoted("-x", inheritDocExclude);

    var result = StartProcess(inheritDoc, new ProcessSettings { Arguments = args });
    
    if (result != 0)
    {
        throw new InvalidOperationException("InheritDoc failed!");
    }

    Information("\nFinished generating documentation with InheritDoc");
});

Task("Package")
	.Description("Pack the NuPkg")
	.IsDependentOn("InheritDoc")
	.Does(() =>
{
	// Invoke the pack target in the end
    var buildSettings = new MSBuildSettings {
        MaxCpuCount = 0
    }
    .SetConfiguration("CI")
    .WithTarget("Pack")
    .WithProperty("GenerateLibraryLayout", "true")
	.WithProperty("PackageOutputPath", nupkgDir);

    MSBuild(Solution, buildSettings);
});



//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("Default")
    .IsDependentOn("Package");

Task("UpdateHeaders")
    .Description("Updates the headers in *.cs files")
    .Does(() =>
{
    VerifyHeaders(true);
});

Task("StyleXaml")
    .Description("Ensures XAML Formatting is Clean")
    .Does(() =>
{
    Information("\nDownloading XamlStyler...");
    var installSettings = new NuGetInstallSettings {
        ExcludeVersion  = true,
        OutputDirectory = toolsDir
    };

    NuGetInstall(new []{"xamlstyler.console"}, installSettings);

    Func<IFileSystemInfo, bool> exclude_objDir =
        fileSystemInfo => !fileSystemInfo.Path.Segments.Contains("obj");

    var files = GetFiles(baseDir + "/**/*.xaml", exclude_objDir);
    Information("\nChecking " + files.Count() + " file(s) for XAML Structure");
    foreach(var file in files)
    {
        StartProcess(styler, "-f \"" + file + "\" -c \"" + stylerFile + "\"");
    }
});



//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(target);
