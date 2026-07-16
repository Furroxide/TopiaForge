using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        private void WriteLastRunReport(Exception? rootError)
        {
            try
            {
                if (startupStopwatch.IsRunning)
                {
                    startupStopwatch.Stop();
                }

                var report = new LastRunReport
                {
                    SessionId = startupJournal?.SessionId ?? string.Empty,
                    StartedAtUtc = startupStartedAtUtc,
                    CompletedAtUtc = DateTime.UtcNow.ToString("O"),
                    StartupDurationMs = startupStopwatch.ElapsedMilliseconds,
                    GameVersion = validationContext.GameVersion ?? string.Empty,
                    LoaderVersion = validationContext.LoaderVersion,
                    SdkVersion = validationContext.SdkVersion,
                    Recovery = startupRecovery.Reason,
                    RootError = rootError?.ToString() ?? string.Empty,
                    RootExceptionChain = DescribeExceptionChain(rootError),
                    Stages = startupStages.ToList()
                };

                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < loadOrder.OrderedPackages.Count; index++)
                {
                    var id = loadOrder.OrderedPackages[index].Manifest?.Id;
                    if (!string.IsNullOrEmpty(id))
                    {
                        order[id] = index;
                    }
                }

                foreach (var package in packages)
                {
                    var id = package.Manifest?.Id ??
                        (Path.GetFileName(Path.GetDirectoryName(package.PackagePath)) ?? Path.GetFileName(package.PackagePath));
                    var failure = runtime?.GetLoadFailure(id);
                    var compatibility = package.Manifest == null
                        ? null
                        : ManifestRuntimeCompatibility.Evaluate(package.Manifest, validationContext);
                    var item = new LastRunPackage
                    {
                        Id = id,
                        Version = package.Manifest?.Version ?? string.Empty,
                        Enabled = package.IsEnabled,
                        Valid = package.IsValid,
                        Compatibility = compatibility?.Status ?? ManifestRuntimeCompatibility.RejectedStatus,
                        CompatibilityReasons = compatibility?.Errors.ToList()
                            ?? new List<string> { "Package manifest was unavailable for compatibility evaluation." },
                        Selection = package.SelectionReason,
                        Status = !package.IsValid
                            ? "invalid"
                            : !package.IsEnabled
                                ? "disabled"
                                : runtime != null && runtime.IsLoaded(id)
                                    ? "loaded"
                                    : failure != null
                                        ? "load-failed"
                                        : "excluded",
                        LoadOrder = order.TryGetValue(id, out var position) ? position : (int?)null,
                        Errors = package.Errors.ToList()
                    };

                    if (loadOrder.Errors.TryGetValue(id, out var resolutionErrors))
                    {
                        item.Errors.AddRange(resolutionErrors);
                    }

                    if (!string.IsNullOrEmpty(failure))
                    {
                        item.Errors.Add(failure);
                    }

                    if (package.Manifest != null)
                    {
                        try
                        {
                            var receipt = JsonUtil.LoadFile(
                                Path.Combine(package.PackagePath, PackageInstallReceipt.FileName),
                                new PackageInstallReceipt());
                            item.SourceSha256 = receipt.SourceSha256 ?? string.Empty;
                            item.CriticalFiles = (receipt.Files ?? new List<PackageFileReceipt>())
                                .Where(file => file != null && file.Critical)
                                .OrderBy(file => file.Path, StringComparer.Ordinal)
                                .Select(file => new LastRunFileDigest
                                {
                                    Path = file.Path,
                                    Sha256 = file.Sha256
                                })
                                .ToList();
                        }
                        catch (Exception ex)
                        {
                            item.Errors.Add("Install receipt could not be summarized: " + ex.Message);
                        }
                    }

                    report.Packages.Add(item);
                }

                report.Packages = report.Packages
                    .OrderBy(item => item.LoadOrder ?? int.MaxValue)
                    .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                JsonUtil.SaveFile(paths.LastRunFile, report);
            }
            catch (Exception ex)
            {
                managerLogger?.Warn("last-run.json could not be written: " + ex.Message);
            }
        }

        private void RecordStartupStage(string name, long startedMs)
        {
            startupStages.Add(new LastRunStage
            {
                Name = name,
                StartedMs = startedMs,
                DurationMs = Math.Max(0, startupStopwatch.ElapsedMilliseconds - startedMs)
            });
        }

        private static List<string> DescribeExceptionChain(Exception? exception)
        {
            var result = new List<string>();
            var current = exception;
            while (current != null && result.Count < 32)
            {
                result.Add(current.GetType().FullName + ": " + current.Message);
                current = current.InnerException;
            }

            return result;
        }

        private sealed class StartupJournalLoadObserver : IModLoadObserver
        {
            private readonly StartupJournal journal;
            private readonly ManagerFileLogger logger;

            public StartupJournalLoadObserver(StartupJournal journal, ManagerFileLogger logger)
            {
                this.journal = journal;
                this.logger = logger;
            }

            public void OnLoading(string modId)
            {
                TryWrite(() => journal.MarkLoading(modId));
            }

            public void OnLoadCompleted(string modId, bool succeeded)
            {
                TryWrite(() => journal.MarkLoaded(modId));
            }

            private void TryWrite(Action write)
            {
                try
                {
                    write();
                }
                catch (Exception ex)
                {
                    logger.Warn("Startup journal update failed: " + ex.Message);
                }
            }
        }
    }
}
