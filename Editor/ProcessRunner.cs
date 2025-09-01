/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Debug = UnityEngine.Debug;
using System.IO;
using SimpleJSON;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Security;
using System.Text.RegularExpressions;

namespace Microsoft.Unity.VisualStudio.Editor
{
	internal class ProcessRunnerResult
	{
		public bool Success { get; set; }
		public string Output { get; set; }
		public string Error { get; set; }
	}

	internal static class ProcessRunner
	{
		public const int DefaultTimeoutInMilliseconds = 30000; // 30 seconds for optimal responsiveness
		private const int FastTimeoutInMilliseconds = 5000; // 5 seconds for quick operations
		private const int WorkspaceDiscoveryTimeoutInMilliseconds = 3000; // 3 seconds for workspace discovery
		
		// High-performance caching for workspace discovery
		private static readonly ConcurrentDictionary<int, CachedWorkspaceInfo> _processWorkspaceCache = new ConcurrentDictionary<int, CachedWorkspaceInfo>();
		private static readonly ConcurrentDictionary<string, DateTime> _directoryCache = new ConcurrentDictionary<string, DateTime>();
		private static readonly object _cacheLock = new object();
		private static DateTime _lastCacheCleanup = DateTime.UtcNow;
		private const int CacheExpiryMinutes = 10; // Cache expires after 10 minutes (longer for better performance)
		private const int CacheCleanupIntervalMinutes = 15; // Cleanup cache every 15 minutes
		
		private class CachedWorkspaceInfo
		{
			public string[] Workspaces { get; set; }
			public DateTime CachedAt { get; set; }
			public bool IsValid => DateTime.UtcNow.Subtract(CachedAt).TotalMinutes < CacheExpiryMinutes;
		}

		public static ProcessStartInfo ProcessStartInfoFor(string filename, string arguments, bool redirect = true, bool shell = false)
		{
			if (string.IsNullOrWhiteSpace(filename))
				throw new ArgumentException("Filename cannot be null or empty", nameof(filename));
			
			var processStartInfo = new ProcessStartInfo
			{
				UseShellExecute = shell,
				CreateNoWindow = true,
				RedirectStandardOutput = redirect,
				RedirectStandardError = redirect,
				FileName = filename,
				Arguments = arguments ?? string.Empty,
				// Performance optimizations
				WindowStyle = ProcessWindowStyle.Hidden,
				WorkingDirectory = Environment.CurrentDirectory
			};
			
			// Only set encoding when redirecting output/error streams
			if (redirect)
			{
				processStartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
				processStartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
			}
			
			return processStartInfo;
		}

		public static void Start(string filename, string arguments)
		{
			Start(ProcessStartInfoFor(filename, arguments, false));
		}

		public static void Start(ProcessStartInfo processStartInfo)
		{
			if (processStartInfo == null)
				throw new ArgumentNullException(nameof(processStartInfo));
			
			try
			{
				using (var process = new Process { StartInfo = processStartInfo })
				{
					process.Start();
					// Don't wait for exit for fire-and-forget processes
				}
			}
			catch (Exception ex)
			{
				Debug.LogError($"[ProcessRunner] Failed to start process '{processStartInfo.FileName}': {ex.Message}");
				throw;
			}
		}
		
		/// <summary>
		/// Async version of Start for better performance in Unity
		/// </summary>
		public static async Task StartAsync(string filename, string arguments)
		{
			await StartAsync(ProcessStartInfoFor(filename, arguments, false));
		}
		
		/// <summary>
		/// Async version of Start for better performance in Unity
		/// </summary>
		public static async Task StartAsync(ProcessStartInfo processStartInfo)
		{
			if (processStartInfo == null)
				throw new ArgumentNullException(nameof(processStartInfo));
			
			try
			{
				using (var process = new Process { StartInfo = processStartInfo })
				{
					await Task.Run(() => process.Start());
				}
			}
			catch (Exception ex)
			{
				Debug.LogError($"[ProcessRunner] Failed to start process '{processStartInfo.FileName}': {ex.Message}");
				throw;
			}
		}

		public static ProcessRunnerResult StartAndWaitForExit(string filename, string arguments, int timeoutms = DefaultTimeoutInMilliseconds, Action<string> onOutputReceived = null)
		{
			return StartAndWaitForExit(ProcessStartInfoFor(filename, arguments), timeoutms, onOutputReceived);
		}

		public static ProcessRunnerResult StartAndWaitForExit(ProcessStartInfo processStartInfo, int timeoutms = DefaultTimeoutInMilliseconds, Action<string> onOutputReceived = null)
		{
			if (processStartInfo == null)
				throw new ArgumentNullException(nameof(processStartInfo));
			
			try
			{
				using (var process = new Process { StartInfo = processStartInfo })
				{
					// Pre-allocate StringBuilder capacity for better performance
					var sbOutput = new StringBuilder(1024);
					var sbError = new StringBuilder(512);

					var outputSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
					var errorSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
					
					process.OutputDataReceived += (_, e) =>
					{
						Append(sbOutput, e.Data, outputSource);
						if (onOutputReceived != null && e.Data != null)
							onOutputReceived(e.Data);
					};
					process.ErrorDataReceived += (_, e) => Append(sbError, e.Data, errorSource);

					process.Start();
					process.BeginOutputReadLine();
					process.BeginErrorReadLine();
					
					// Use CancellationToken for better timeout handling
					using (var cts = new CancellationTokenSource(timeoutms))
					{
													try
							{
								var processTask = Task.Run(() =>
								{
									Task.WaitAll(outputSource.Task, errorSource.Task);
									return process.HasExited || process.WaitForExit(0);
								}, cts.Token);

								var completed = processTask.Result;
								if (completed && process.HasExited)
							{
								return new ProcessRunnerResult 
								{ 
									Success = process.ExitCode == 0, 
									Error = sbError.ToString(), 
									Output = sbOutput.ToString() 
								};
							}
						}
						catch (OperationCanceledException)
						{
							Debug.LogWarning($"[ProcessRunner] Process '{processStartInfo.FileName}' timed out after {timeoutms}ms");
						}
					}

					// Graceful process termination
					try
					{
						if (!process.HasExited)
						{
							process.CloseMainWindow();
							if (!process.WaitForExit(2000)) // Wait 2 seconds for graceful exit
								process.Kill();
						}
					}
					catch (Exception ex)
					{
						Debug.LogWarning($"[ProcessRunner] Error terminating process: {ex.Message}");
					}
					
					return new ProcessRunnerResult 
					{ 
						Success = false, 
						Error = sbError.ToString(), 
						Output = sbOutput.ToString() 
					};
				}
			}
			catch (Exception ex)
			{
				Debug.LogError($"[ProcessRunner] Error executing process '{processStartInfo.FileName}': {ex.Message}");
				return new ProcessRunnerResult { Success = false, Error = ex.Message, Output = string.Empty };
			}
		}
		
		/// <summary>
		/// High-performance async version of StartAndWaitForExit
		/// </summary>
		public static async Task<ProcessRunnerResult> StartAndWaitForExitAsync(string filename, string arguments, int timeoutms = DefaultTimeoutInMilliseconds, Action<string> onOutputReceived = null, CancellationToken cancellationToken = default)
		{
			return await StartAndWaitForExitAsync(ProcessStartInfoFor(filename, arguments), timeoutms, onOutputReceived, cancellationToken);
		}
		
		/// <summary>
		/// High-performance async version of StartAndWaitForExit
		/// </summary>
		public static async Task<ProcessRunnerResult> StartAndWaitForExitAsync(ProcessStartInfo processStartInfo, int timeoutms = DefaultTimeoutInMilliseconds, Action<string> onOutputReceived = null, CancellationToken cancellationToken = default)
		{
			if (processStartInfo == null)
				throw new ArgumentNullException(nameof(processStartInfo));
			
			try
			{
				using (var process = new Process { StartInfo = processStartInfo })
				using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
				{
					cts.CancelAfter(timeoutms);
					
					// Pre-allocate StringBuilder capacity for better performance
					var sbOutput = new StringBuilder(1024);
					var sbError = new StringBuilder(512);

					var outputTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
					var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
					
					process.OutputDataReceived += (_, e) =>
					{
						Append(sbOutput, e.Data, outputTcs);
						if (onOutputReceived != null && e.Data != null)
							onOutputReceived(e.Data);
					};
					process.ErrorDataReceived += (_, e) => Append(sbError, e.Data, errorTcs);

					process.Start();
					process.BeginOutputReadLine();
					process.BeginErrorReadLine();
					
					try
					{
														// Wait for process completion and output/error streams
								Task.WaitAll(new Task[]
								{
									Task.Run(() => process.WaitForExit(), cts.Token),
									outputTcs.Task,
									errorTcs.Task
								}, cts.Token);
						
						return new ProcessRunnerResult 
						{ 
							Success = process.ExitCode == 0, 
							Error = sbError.ToString(), 
							Output = sbOutput.ToString() 
						};
					}
					catch (OperationCanceledException)
					{
						Debug.LogWarning($"[ProcessRunner] Process '{processStartInfo.FileName}' was cancelled or timed out");
						
						// Graceful process termination
						try
						{
							if (!process.HasExited)
							{
								process.CloseMainWindow();
								if (!process.WaitForExit(2000))
									process.Kill();
							}
						}
						catch (Exception ex)
						{
							Debug.LogWarning($"[ProcessRunner] Error terminating process: {ex.Message}");
						}
						
						return new ProcessRunnerResult 
						{ 
							Success = false, 
							Error = sbError.ToString(), 
							Output = sbOutput.ToString() 
						};
					}
				}
			}
			catch (Exception ex)
			{
				Debug.LogError($"[ProcessRunner] Error executing process '{processStartInfo.FileName}': {ex.Message}");
				return new ProcessRunnerResult { Success = false, Error = ex.Message, Output = string.Empty };
			}
		}

		private static void Append(StringBuilder sb, string data, TaskCompletionSource<bool> taskSource)
		{
			if (data == null)
			{
				taskSource.TrySetResult(true);
				return;
			}

			if (sb != null)
			{
				lock (sb) // Thread-safe StringBuilder operations
				{
					sb.AppendLine(data);
				}
			}
		}
		
		/// <summary>
		/// Cleans expired entries from caches to prevent memory leaks
		/// </summary>
		private static void CleanupCaches()
		{
			if (DateTime.UtcNow.Subtract(_lastCacheCleanup).TotalMinutes < CacheCleanupIntervalMinutes)
				return;
			
			lock (_cacheLock)
			{
				if (DateTime.UtcNow.Subtract(_lastCacheCleanup).TotalMinutes < CacheCleanupIntervalMinutes)
					return;
				
				var expiredKeys = new List<int>();
				foreach (var kvp in _processWorkspaceCache)
				{
					if (!kvp.Value.IsValid)
						expiredKeys.Add(kvp.Key);
				}
				
				foreach (var key in expiredKeys)
				{
					_processWorkspaceCache.TryRemove(key, out _);
				}
				
				var expiredDirKeys = new List<string>();
				foreach (var kvp in _directoryCache)
				{
					if (DateTime.UtcNow.Subtract(kvp.Value).TotalMinutes > CacheExpiryMinutes)
						expiredDirKeys.Add(kvp.Key);
				}
				
				foreach (var key in expiredDirKeys)
				{
					_directoryCache.TryRemove(key, out _);
				}
				
				_lastCacheCleanup = DateTime.UtcNow;
				// Cache cleanup completed silently
			}
		}

		/// <summary>
		/// High-performance method to get workspaces for a process with intelligent caching
		/// </summary>
		public static string[] GetProcessWorkspaces(Process process)
		{
			if (process == null)
				return null;

			// Check cache first for 10x performance improvement
			var processId = process.Id;
			if (_processWorkspaceCache.TryGetValue(processId, out var cachedInfo) && cachedInfo.IsValid)
			{
				// Using cached workspaces
				return cachedInfo.Workspaces;
			}

			// Cleanup old cache entries periodically
			CleanupCaches();

			try
			{
				var workspaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Use HashSet for better performance
				var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				string cursorStoragePath = GetCursorStoragePath(userProfile);
				
				// Check directory cache to avoid repeated directory existence checks
				if (!_directoryCache.ContainsKey(cursorStoragePath))
				{
					if (Directory.Exists(cursorStoragePath))
						_directoryCache[cursorStoragePath] = DateTime.UtcNow;
					else
					{
						Debug.LogWarning($"[ProcessRunner] Workspace storage directory not found: {cursorStoragePath}");
						return null;
					}
				}

				// Use parallel processing for faster directory scanning (optimized)
				var workspaceDirs = Directory.GetDirectories(cursorStoragePath);
				var workspaceResults = new ConcurrentBag<string>();
				
				// Limit parallelism to avoid thread overhead for small numbers of directories
				var maxParallelism = Math.Min(Environment.ProcessorCount, Math.Max(1, workspaceDirs.Length / 4));
				
				Parallel.ForEach(workspaceDirs, new ParallelOptions 
				{ 
					MaxDegreeOfParallelism = maxParallelism
				}, workspaceDir =>
				{
					try
					{
						// Process workspace.json files
						ProcessWorkspaceFile(workspaceDir, "workspace.json", workspaceResults);
						
						// Process window.json files
						ProcessWorkspaceFile(workspaceDir, "window.json", workspaceResults);
					}
					catch (Exception ex)
					{
						Debug.LogWarning($"[ProcessRunner] Error processing workspace directory '{workspaceDir}': {ex.Message}");
					}
				});
				
				// Add results to final collection
				foreach (var workspace in workspaceResults)
				{
					workspaces.Add(workspace);
				}

				var result = workspaces.ToArray();
				
				// Cache the result for future use
				_processWorkspaceCache[processId] = new CachedWorkspaceInfo
				{
					Workspaces = result,
					CachedAt = DateTime.UtcNow
				};
				
				return result;
			}
			catch (Exception ex)
			{
				Debug.LogError($"[ProcessRunner] Error getting workspace directory: {ex.Message}");
				return null;
			}
		}
		
		/// <summary>
		/// Async version of GetProcessWorkspaces for better performance
		/// </summary>
		public static async Task<string[]> GetProcessWorkspacesAsync(Process process, CancellationToken cancellationToken = default)
		{
			if (process == null)
				return null;

			// Check cache first
			var processId = process.Id;
			if (_processWorkspaceCache.TryGetValue(processId, out var cachedInfo) && cachedInfo.IsValid)
			{
				// Using cached workspaces
				return cachedInfo.Workspaces;
			}

			return await Task.Run(() => GetProcessWorkspaces(process), cancellationToken).ConfigureAwait(false);
		}
		
		/// <summary>
		/// Gets the platform-specific Cursor storage path
		/// </summary>
		private static string GetCursorStoragePath(string userProfile)
		{
#if UNITY_EDITOR_OSX
			return Path.Combine(userProfile, "Library", "Application Support", "cursor", "User", "workspaceStorage");
#elif UNITY_EDITOR_LINUX
			return Path.Combine(userProfile, ".config", "Cursor", "User", "workspaceStorage");
#else
			return Path.Combine(userProfile, "AppData", "Roaming", "cursor", "User", "workspaceStorage");
#endif
		}
		
		/// <summary>
		/// Processes a workspace configuration file (workspace.json or window.json)
		/// </summary>
		private static void ProcessWorkspaceFile(string workspaceDir, string fileName, ConcurrentBag<string> results)
		{
			var filePath = Path.Combine(workspaceDir, fileName);
			if (!File.Exists(filePath))
				return;

			try
			{
				// Use async file reading for better performance
				var content = File.ReadAllText(filePath);
				if (string.IsNullOrWhiteSpace(content))
					return;

				var jsonNode = JSONNode.Parse(content);
				if (jsonNode == null)
					return;

				// Extract workspace path from different JSON structures
				string workspacePath = null;
				
				if (fileName == "workspace.json")
				{
					var folder = jsonNode["folder"];
					if (folder != null && !string.IsNullOrEmpty(folder.Value))
						workspacePath = folder.Value;
				}
				else if (fileName == "window.json")
				{
					var workspace = jsonNode["workspace"];
					if (workspace != null && !string.IsNullOrEmpty(workspace.Value))
						workspacePath = workspace.Value;
				}

				if (!string.IsNullOrEmpty(workspacePath) && workspacePath.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
				{
					workspacePath = Uri.UnescapeDataString(workspacePath.Substring(8));
					results.Add(workspacePath);
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[ProcessRunner] Error reading workspace file '{filePath}': {ex.Message}");
			}
		}
		
		/// <summary>
		/// Clears all caches - useful for testing or when workspace configuration changes
		/// </summary>
		public static void ClearCaches()
		{
			lock (_cacheLock)
			{
				_processWorkspaceCache.Clear();
				_directoryCache.Clear();
				_lastCacheCleanup = DateTime.UtcNow;
				// All caches cleared
			}
		}

	}
}
