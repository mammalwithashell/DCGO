using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Digimon.Harness.EditorTools
{
    /// <summary>
    /// Headless standalone build of DCGO with the harness mod, so an agent can
    /// run the oracle without an Editor session.
    /// </summary>
    /// <remarks>
    /// Invoked as:
    ///   Unity.exe -quit -batchmode -nographics -projectPath &lt;DCGO&gt;
    ///     -executeMethod Digimon.Harness.EditorTools.HarnessBuild.Build
    ///     -harnessBuildOutput D:\dcgo-build\&lt;version&gt; -logFile -
    ///
    /// The output directory is passed on the command line rather than baked in
    /// because the host CLI owns versioning: it picks the directory, then hashes
    /// what lands there. Baking a path here would split that ownership across
    /// two languages.
    /// </remarks>
    public static class HarnessBuild
    {
        private const string OutputArg = "-harnessBuildOutput";
        private const string ExecutableName = "DCGO.exe";

        public static void Build()
        {
            try
            {
                string outputDir = ReadOutputArg();
                if (string.IsNullOrEmpty(outputDir))
                {
                    Fail("missing " + OutputArg + " <path> on the command line");
                    return;
                }

                Directory.CreateDirectory(outputDir);

                string[] scenes = EditorBuildSettings.scenes
                    .Where(s => s.enabled)
                    .Select(s => s.path)
                    .ToArray();

                if (scenes.Length == 0)
                {
                    Fail("no enabled scenes in EditorBuildSettings; nothing to build");
                    return;
                }

                Debug.Log("[HarnessBuild] building " + scenes.Length + " scene(s) -> " + outputDir);

                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = Path.Combine(outputDir, ExecutableName),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None,
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                if (summary.result == BuildResult.Succeeded)
                {
                    Debug.Log("[HarnessBuild] OK: " + summary.totalSize + " bytes -> "
                              + summary.outputPath);
                    EditorApplication.Exit(0);
                    return;
                }

                // Surface the first few errors inline. In batchmode the log is
                // the only artifact, and BuildReport's own summary says only
                // "Failed" with a count.
                foreach (var step in report.steps)
                {
                    foreach (var msg in step.messages)
                    {
                        if (msg.type == LogType.Error || msg.type == LogType.Exception)
                        {
                            Debug.LogError("[HarnessBuild] " + step.name + ": " + msg.content);
                        }
                    }
                }

                Fail("build result " + summary.result + " with "
                     + summary.totalErrors + " error(s)");
            }
            catch (Exception e)
            {
                Fail("threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static string ReadOutputArg()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == OutputArg)
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        private static void Fail(string reason)
        {
            // EditorApplication.Exit is what makes -quit honour a nonzero code.
            // Throwing instead would exit 0 and report a broken build as a
            // successful one -- the exact silent-pass shape the harness's
            // denominator rules exist to prevent.
            Debug.LogError("[HarnessBuild] FAILED: " + reason);
            EditorApplication.Exit(1);
        }
    }
}
