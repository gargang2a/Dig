#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class TmpBuildAutomation
{
    [MenuItem("Tools/DigWar/Build/Tmp Build Server + WebGL")]
    public static void BuildServerAndWebGl()
    {
        BuildServer();
        BuildWebGl();
        AssetDatabase.Refresh();
        Debug.Log("[TmpBuild] Completed server + WebGL build.");
    }

    public static void BuildServer()
    {
        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length == 0)
            throw new System.Exception("[TmpBuild] No enabled scenes in Build Settings.");

        string outputPath = Path.GetFullPath("Build/DigWarServer/DigWar.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            target = BuildTarget.StandaloneWindows64,
            subtarget = (int)StandaloneBuildSubtarget.Server,
            locationPathName = outputPath,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new System.Exception($"[TmpBuild] Server build failed: {report.summary.result}");

        Debug.Log(
            $"[TmpBuild] Server build succeeded. " +
            $"output={outputPath}, size={report.summary.totalSize}, time={report.summary.totalTime.TotalSeconds:F1}s");
    }

    public static void BuildWebGl()
    {
        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length == 0)
            throw new System.Exception("[TmpBuild] No enabled scenes in Build Settings.");

        string outputDir = Path.GetFullPath("build/WebGL");
        Directory.CreateDirectory(outputDir);

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            target = BuildTarget.WebGL,
            locationPathName = outputDir,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new System.Exception($"[TmpBuild] WebGL build failed: {report.summary.result}");

        Debug.Log(
            $"[TmpBuild] WebGL build succeeded. " +
            $"output={outputDir}, size={report.summary.totalSize}, time={report.summary.totalTime.TotalSeconds:F1}s");
    }
}
#endif
