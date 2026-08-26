#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Ruitk.Builder;
using UnityEditor;
using UnityEngine;

namespace RuitkParityHarness
{
    /// <summary>
    /// Dev-only capture harness for the POC-parity loop. NOT part of the shipped
    /// package — it lives in the consumer project's Assets and is copied there
    /// from Plans~/capture-harness/.
    ///
    /// Protocol (files under %TEMP%\ruitk-parity\unity):
    ///   jobs.json        written by the driver: [{ "name", "file", "zoom" }]
    ///   ready-N.txt      written by Unity once state N is on screen
    ///   captured-N.txt   written by the driver once it has the screenshot
    ///   done.txt         written by Unity when the job list is exhausted
    ///   error.txt        written by Unity if a job could not be staged
    ///
    /// The window is floated at a fixed rect so the driver can crop
    /// deterministically, and each state's zoom is staged by writing the
    /// builder's own per-tree config before the window opens — no input
    /// synthesis, no internal APIs.
    /// </summary>
    [InitializeOnLoad]
    public static class RuitkParityCaptureHarness
    {
        private const string WindowRectKey = "Ruitk.Parity.WindowRect";
        private static readonly Rect CaptureRect = new Rect(60f, 60f, 1720f, 980f);

        private static string Root =>
            Path.Combine(Path.GetTempPath(), "ruitk-parity", "unity");

        [Serializable]
        private sealed class Job
        {
            public string name;
            public string file;
            public float zoom = 0.75f;
        }

        [Serializable]
        private sealed class JobList
        {
            public List<Job> jobs = new List<Job>();
        }

        private enum Phase
        {
            Idle,
            Staging,
            Settling,
            AwaitingCapture,
        }

        private static Phase s_phase = Phase.Idle;
        private static List<Job> s_jobs;
        private static int s_index;
        private static double s_settleUntil;
        private static double s_waitingSince;

        static RuitkParityCaptureHarness()
        {
            EditorApplication.update += Tick;
        }

        [MenuItem("Reactive UI Toolkit/Diagnostics/Parity Capture/Show Capture Folder")]
        private static void ShowFolder()
        {
            Directory.CreateDirectory(Root);
            EditorUtility.RevealInFinder(Root);
        }

        private static void Tick()
        {
            try
            {
                switch (s_phase)
                {
                    case Phase.Idle:
                        PollForJobs();
                        break;
                    case Phase.Staging:
                        StageCurrent();
                        break;
                    case Phase.Settling:
                        if (EditorApplication.timeSinceStartup >= s_settleUntil)
                        {
                            File.WriteAllText(
                                Path.Combine(Root, "ready-" + s_index + ".txt"),
                                s_jobs[s_index].name);
                            s_waitingSince = EditorApplication.timeSinceStartup;
                            s_phase = Phase.AwaitingCapture;
                        }
                        break;
                    case Phase.AwaitingCapture:
                        // The driver can die mid-run; never wait on it forever.
                        if (EditorApplication.timeSinceStartup - s_waitingSince > 90.0)
                            throw new TimeoutException("capture driver stopped responding");
                        if (File.Exists(Path.Combine(Root, "captured-" + s_index + ".txt")))
                        {
                            s_index++;
                            SessionState.SetInt(IndexKey, s_index);
                            if (s_index < s_jobs.Count)
                                s_phase = Phase.Staging;
                            else
                                FinishRun("ok");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Directory.CreateDirectory(Root);
                    File.WriteAllText(Path.Combine(Root, "error.txt"), ex.ToString());
                    FinishRun("error");
                }
                catch
                {
                    s_phase = Phase.Idle;
                    s_jobs = null;
                }
            }
        }

        /// <summary>A trigger older than this is a leftover from a driver that
        /// already gave up — honouring it would pop builder windows open in the
        /// owner's editor and then hang waiting for a capture that never comes.</summary>
        private const double TriggerMaxAgeSeconds = 120.0;

        // Opening the builder compiles/imports, and the domain reload that
        // follows resets every static in this class. Progress therefore lives in
        // SessionState (which survives reloads) and jobs.json is kept until the
        // whole run finishes, so a reload resumes instead of silently dropping
        // the remaining jobs — the failure that looked like "it opened the
        // window once and hung".
        private const string StampKey = "Ruitk.Parity.RunStamp";
        private const string IndexKey = "Ruitk.Parity.RunIndex";

        private static void PollForJobs()
        {
            string jobsPath = Path.Combine(Root, "jobs.json");
            if (!File.Exists(jobsPath))
                return;

            string stamp = File.GetLastWriteTimeUtc(jobsPath).Ticks.ToString();
            bool resuming = SessionState.GetString(StampKey, "") == stamp;

            if (!resuming)
            {
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(jobsPath)).TotalSeconds
                    > TriggerMaxAgeSeconds)
                {
                    File.Delete(jobsPath);
                    return;
                }
                foreach (string stale in Directory.GetFiles(Root, "*.txt"))
                    File.Delete(stale);
                SessionState.SetString(StampKey, stamp);
                SessionState.SetInt(IndexKey, 0);
            }

            var list = JsonUtility.FromJson<JobList>(File.ReadAllText(jobsPath));
            if (list == null || list.jobs == null || list.jobs.Count == 0)
            {
                File.Delete(jobsPath);
                File.WriteAllText(Path.Combine(Root, "done.txt"), "empty");
                return;
            }

            s_jobs = list.jobs;
            s_index = Mathf.Clamp(SessionState.GetInt(IndexKey, 0), 0, s_jobs.Count - 1);

            // If the reload landed after the driver already acked a capture, skip
            // ahead — otherwise Unity re-stages a job the driver has moved past
            // and both sides wait on each other forever.
            while (s_index < s_jobs.Count
                && File.Exists(Path.Combine(Root, "captured-" + s_index + ".txt")))
                s_index++;

            if (s_index >= s_jobs.Count)
            {
                FinishRun("ok");
                return;
            }
            SessionState.SetInt(IndexKey, s_index);
            s_phase = Phase.Staging;
        }

        private static void FinishRun(string status)
        {
            string jobsPath = Path.Combine(Root, "jobs.json");
            if (File.Exists(jobsPath))
                File.Delete(jobsPath);
            SessionState.EraseString(StampKey);
            SessionState.EraseInt(IndexKey);
            File.WriteAllText(Path.Combine(Root, "done.txt"), status);
            s_jobs = null;
            s_phase = Phase.Idle;
        }

        private static void StageCurrent()
        {
            var job = s_jobs[s_index];
            string file = Path.GetFullPath(job.file);
            if (!File.Exists(file))
                throw new FileNotFoundException("capture job target missing", file);

            StageZoom(file, job.zoom);

            foreach (var open in Resources.FindObjectsOfTypeAll<BuilderWindow>())
                open.Close();

            var window = BuilderWindow.OpenFor(file);
            window.position = CaptureRect;
            window.Focus();
            window.Repaint();

            SessionState.SetString(
                WindowRectKey,
                string.Join(",", new[]
                {
                    CaptureRect.x.ToString(), CaptureRect.y.ToString(),
                    CaptureRect.width.ToString(), CaptureRect.height.ToString(),
                }));

            // The canvas populates asynchronously (LSP spawn + workspace graph),
            // so a frame count would photograph "Loading tree…". The first open
            // pays the language-server cold start; later ones reuse it.
            s_settleUntil = EditorApplication.timeSinceStartup + (s_index == 0 ? 14.0 : 5.0);
            s_phase = Phase.Settling;
        }

        /// <summary>Writes the builder's own per-tree canvas config so the canvas
        /// boots at the requested zoom — the states the POC reaches with its
        /// L0/L1/L2 buttons, staged without synthesizing input.</summary>
        private static void StageZoom(string file, float zoom)
        {
            string dir = Path.Combine(
                Path.GetDirectoryName(Application.dataPath) ?? ".",
                "UserSettings", "ReactiveUIToolkit", "Builder");
            Directory.CreateDirectory(dir);

            using var sha = SHA1.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(file.ToLowerInvariant()));
            string key = BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
            string path = Path.Combine(dir, key + ".json");

            string existing = File.Exists(path) ? File.ReadAllText(path) : null;
            string json = existing != null && existing.Contains("\"Zoom\"")
                ? System.Text.RegularExpressions.Regex.Replace(
                    existing,
                    "\"Zoom\"\\s*:\\s*[0-9.]+",
                    "\"Zoom\": " + zoom.ToString("0.###"))
                : "{\n  \"RootPath\": " + Quote(file)
                    + ",\n  \"Members\": [" + Quote(file) + "],"
                    + "\n  \"Positions\": {},\n  \"CameraX\": 60.0,\n  \"CameraY\": 30.0,"
                    + "\n  \"Zoom\": " + zoom.ToString("0.###") + "\n}";
            File.WriteAllText(path, json);
        }

        private static string Quote(string value) =>
            "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
#endif
