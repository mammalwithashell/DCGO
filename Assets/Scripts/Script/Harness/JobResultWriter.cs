using System;
using System.IO;
using UnityEngine;

namespace Digimon.Harness
{
    /// <summary>
    /// Writes a job's result sidecar and moves the claimed job file to done/.
    /// </summary>
    // [Harness mod] New file. This is the success/partial counterpart to
    // JobWatcher.Fail -- it is what lets the harness play more than one job.
    // Before this existed, JobWatcher.CurrentJob was set when a job started
    // and cleared ONLY inside Fail. PollLoop only calls TryClaimAndStart when
    // CurrentJob == null, so a job that actually finished (rather than
    // failing) left CurrentJob set forever: job 2..N were never claimed, the
    // GManager.AwakeCoroutine harness guard (CurrentJob != null) stayed true
    // forever and force-enabled auto mode on a later human-started game, and
    // the idle-poll ClearOverrides() in JobWatcher became unreachable so the
    // deck overrides and Time.timeScale leaked for the process lifetime.
    public static class JobResultWriter
    {
        /// <param name="outcome">"completed", "partial", or "failed".</param>
        public static void FileResult(string outcome, int steps, string message)
        {
            JobWatcher watcher = JobWatcher.Instance;
            if (watcher == null || watcher.CurrentJob == null) return;

            // [Harness mod] Everything below runs inside try/finally so
            // watcher.ClearCurrentJob() ALWAYS fires -- on a clean write, on a
            // caught file-I/O exception, or on any other exception thrown
            // while building the json string (a bad job_id, a throwing
            // CurrentRecordingPath getter, etc.), which the inner try below
            // does not cover. Without this guarantee, ANY exception here would
            // leave CurrentJob non-null forever and reproduce exactly the
            // stall this task exists to close (see the class remark above).
            try
            {
                string jobId = watcher.CurrentJob.job_id;
                double seconds = (DateTime.UtcNow - watcher.StartedUtc).TotalSeconds;
                string recordingPath = Digimon.Recording.GameRecorder.Instance != null
                    ? Digimon.Recording.GameRecorder.Instance.CurrentRecordingPath
                    : "";

                // Hand-built JSON: JsonUtility cannot emit the snake_case shape the
                // Rust reader expects without a mirror DTO, and this is six fields.
                string json =
                    "{\n" +
                    "  \"job_id\": " + Quote(jobId) + ",\n" +
                    "  \"outcome\": " + Quote(outcome) + ",\n" +
                    "  \"recording_path\": " + Quote(recordingPath) + ",\n" +
                    "  \"steps\": " + steps + ",\n" +
                    "  \"duration_seconds\": " + seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ",\n" +
                    "  \"message\": " + Quote(message ?? "") + "\n" +
                    "}\n";

                try
                {
                    Directory.CreateDirectory(HarnessConfig.DoneDir);
                    File.WriteAllText(Path.Combine(HarnessConfig.DoneDir, jobId + ".result.json"), json);

                    if (!string.IsNullOrEmpty(watcher.ClaimedPath) && File.Exists(watcher.ClaimedPath))
                    {
                        string dest = Path.Combine(HarnessConfig.DoneDir, Path.GetFileName(watcher.ClaimedPath));
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(watcher.ClaimedPath, dest);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("[Harness] filing result failed: " + e.Message);
                }
            }
            finally
            {
                // [Harness mod] Release the job on every exit path from the
                // outer try -- see the remark above FileResult.
                watcher.ClearCurrentJob();
            }
        }

        private static string Quote(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
