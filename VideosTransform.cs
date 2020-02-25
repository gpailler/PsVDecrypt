using System.Collections.Concurrent;

namespace PsVDecrypt
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Net.Http;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Newtonsoft.Json;

    public class VideosTransform
    {
        private static readonly Version MinimalFFmpegVersion = new Version(4, 2);
        private static readonly Uri FFmpegBinaries = new Uri("https://github.com/vot/ffbinaries-prebuilt/releases/download/v4.2.1/ffmpeg-4.2.1-win-64.zip");

        public bool ValidateFFmpeg()
        {
            Console.WriteLine($"> Searching for FFmpeg >= {MinimalFFmpegVersion} in current directory and in PATH");

            var (success, log) = RunFFmpeg("-version");
            if (success)
            {
                var versionMatch = Regex.Match(
                    log,
                    @"^ffmpeg version (?<version>[\d\.]+) ",
                    RegexOptions.Multiline);

                if (versionMatch.Success)
                {
                    var version = Version.Parse(versionMatch.Groups["version"].Value);
                    bool isSupported = version >= MinimalFFmpegVersion;
                    Console.WriteLine(isSupported
                        ? $"> FFmpeg {version} found"
                        : $"> Unsupported FFmpeg version found. {version} or higher required");

                    return isSupported;
                }
            }

            Console.WriteLine($"> FFmpeg not found");
            return false;
        }

        public async Task DownloadFFmpegIfRequiredAsync()
        {
            var tmpFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            try
            {
                using (var httpClient = new HttpClient())
                {
                    var response = await httpClient.GetAsync(FFmpegBinaries, HttpCompletionOption.ResponseHeadersRead);

                    var fileSizeMb = response.Content.Headers.ContentLength.GetValueOrDefault() / 1024 / 1024;
                    Console.WriteLine($"> Downloading {FFmpegBinaries} ({fileSizeMb} MB)...");
                    response.EnsureSuccessStatusCode();
                    using (var fs = new FileStream(tmpFile, FileMode.CreateNew))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }

                var executingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                Console.WriteLine($"> Extracting FFmpeg to {executingDirectory}");
                ZipFile.ExtractToDirectory(tmpFile, executingDirectory);
            }
            finally
            {
                this.TryDeleteFile(tmpFile);
            }
        }

        public void ProcessCourse(string courseDirectory)
        {
            var allClips = Directory
                .GetFiles(courseDirectory, "*.mp4", SearchOption.AllDirectories);
            var allClipsWithDuration = new ConcurrentDictionary<string, TimeSpan>();

            // Merge clips + transcripts and retrieve clips duration
            Parallel.ForEach(
                allClips.OrderBy(x => x),
                new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount },
                (clipFile, state, idx) =>
            {
                Console.WriteLine($"> Processing clip '{clipFile.Substring(courseDirectory.Length + 1)}'");
                this.MergeClipAndTranscript(clipFile);
                allClipsWithDuration[clipFile] = this.RetrieveVideoDuration(clipFile);
            });

            // Merge videos in modules + add metadata
            foreach (var moduleDirectory in Directory.GetDirectories(courseDirectory))
            {
                var moduleName = Path.GetFileName(moduleDirectory);
                Console.WriteLine($"> Merging clips from '{moduleName}' module");

                var orderedClips = allClipsWithDuration
                    .Where(x => x.Key.StartsWith(moduleDirectory, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.Key);

                var outputVideo = this.MergeClips(orderedClips, moduleDirectory);
                if (outputVideo != null)
                {
                    Util.DeleteDirectory(moduleDirectory);
                }
            }
        }

        private string MergeClips(IEnumerable<KeyValuePair<string, TimeSpan>> orderedClips, string moduleDirectory)
        {
            var outputVideo = moduleDirectory + ".mp4";
            File.Delete(outputVideo);

            // Create list of clips
            var clipsList = Path.GetTempFileName();
            File.WriteAllLines(clipsList, orderedClips.Select(x => $"file '{x.Key}'"));

            // Create metadata
            var metadataFile = this.GenerateMetadataFile(moduleDirectory, orderedClips);

            var arguments = new[]
            {
                $"-f concat",
                $"-safe 0",
                $"-i {clipsList}",
                $"-i {metadataFile}",
                "-map_metadata 1",
                "-c:v copy -c:a copy -c:s copy",
                "-f mp4",
                $"\"{outputVideo}\""
            };

            var (success, log) = this.RunFFmpeg(arguments);

            File.Delete(clipsList);
            File.Delete(metadataFile);

            if (success)
            {
                return outputVideo;
            }
            else
            {
                Console.WriteLine($"> Error when merging videos");
                return null;
            }
        }

        private void MergeClipAndTranscript(string videoFile)
        {
            var transcript = videoFile + ".srt";

            var tempVideo = videoFile + ".tmp";
            File.Delete(tempVideo);

            if (File.Exists(transcript))
            {
                var arguments = new[]
                {
                    $"-i \"{videoFile}\"",
                    $"-i \"{transcript}\"",
                    "-c:a copy -c:v copy -c:s mov_text",
                    "-f mp4",
                    $"\"{tempVideo}\""
                };

                var (success, log) = RunFFmpeg(arguments.ToArray());
                {
                    if (success)
                    {
                        File.Delete(videoFile);
                        File.Move(tempVideo, videoFile);
                        File.Delete(transcript);
                    }
                    else
                    {
                        Console.WriteLine($"> Error when processing video");
                    }
                }
            }
        }

        private TimeSpan RetrieveVideoDuration(string videoFile)
        {
            var arguments = new[]
            {
                $"-i \"{videoFile}\"",
                "-c copy",
                "-f null",
                "-"
            };

            var (success, log) = RunFFmpeg(arguments.ToArray());
            {
                if (success)
                {
                    var match = Regex.Matches(log, @"Duration: (?<duration>[^,]+)")
                        .OfType<Match>()
                        .First();
                    if (match.Success)
                    {
                        return TimeSpan.Parse(match.Groups["duration"].Value);
                    }
                }
            }

            throw new Exception("Unable to retrieve video duration");
        }

        private string GenerateMetadataFile(string moduleDirectory, IEnumerable<KeyValuePair<string, TimeSpan>> clipsWithDuration)
        {
            var courseDefinition = new[] { new {AuthorsFullnames = "", Title = ""} };
            var courseData = JsonConvert.DeserializeAnonymousType(
                File.ReadAllText(Path.Combine(moduleDirectory, "..", "course-info.json")),
                courseDefinition);

            var moduleDefinition = new { Title = ""};
            var moduleData = JsonConvert.DeserializeAnonymousType(
                File.ReadAllText(Path.Combine(moduleDirectory, "module-info.json")),
                moduleDefinition);

            long offset = 0;
            List<string> data = new List<string>();
            data.Add(";FFMETADATA1");
            data.Add($"title={courseData[0].Title} - {moduleData.Title}");
            data.Add("artist=" + courseData[0].AuthorsFullnames.Trim());

            foreach (var clip in clipsWithDuration)
            {
                data.Add("[CHAPTER]");
                data.Add("TIMEBASE=1/1000");
                data.Add($"START={offset}");
                data.Add($"END={offset + clip.Value.TotalMilliseconds - 1}");
                data.Add($"title={Path.GetFileNameWithoutExtension(clip.Key)}");
                data.Add("");
                offset += (int)clip.Value.TotalMilliseconds;
            }

            data.Add("[STREAM]");
            data.Add("title=" + moduleData.Title);

            var file = Path.GetTempFileName();
            File.WriteAllLines(file, data);
            return file;
        }

        private void TryDeleteFile(string fileName)
        {
            if (File.Exists(fileName))
            {
                File.Delete(fileName);
            }
        }

        private (bool success, string log) RunFFmpeg(params string[] arguments)
        {
            var log = new StringBuilder();

            var startInfo = new ProcessStartInfo("ffmpeg", string.Join(" ", arguments))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(startInfo))
            {
                process.OutputDataReceived += (s, e) => log.AppendLine(e.Data);
                process.ErrorDataReceived += (s, e) => log.AppendLine(e.Data);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                return (process.ExitCode == 0, log.ToString());
            }
        }
    }
}