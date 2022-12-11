using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AppsTester.Checker.Android.Adb;
using AppsTester.Checker.Android.Apk;
using AppsTester.Checker.Android.Devices;
using AppsTester.Checker.Android.Gradle;
using AppsTester.Checker.Android.Instrumentations;
using AppsTester.Checker.Android.Results;
using AppsTester.Checker.Android.Statuses;
using AppsTester.Shared.Files;
using AppsTester.Shared.SubmissionChecker;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;
using SharpAdbClient;
using SharpAdbClient.DeviceCommands;

namespace AppsTester.Checker.Android
{
    // ReSharper disable once ClassNeverInstantiated.Global
    internal class AndroidApplicationSubmissionChecker : SubmissionChecker
    {
        private const int SimultaneousTestsCount = 3;

        private readonly IAdbClientProvider _adbClientProvider;
        private readonly IGradleRunner _gradleRunner;
        private readonly IInstrumentationsOutputParser _instrumentationsOutputParser;
        private readonly ISubmissionProcessingLogger _logger;
        private readonly ITemporaryFolderProvider _temporaryFolderProvider;
        private readonly IReservedDevicesProvider _reservedDevicesProvider;
        private readonly IApkReader _apkReader;

        private readonly ISubmissionFilesProvider _filesProvider;
        private readonly ISubmissionStatusSetter _submissionStatusSetter;

        public AndroidApplicationSubmissionChecker(IAdbClientProvider adbClientProvider,
            IGradleRunner gradleRunner,
            IInstrumentationsOutputParser instrumentationsOutputParser,
            ITemporaryFolderProvider temporaryFolderProvider,
            ISubmissionStatusSetter submissionStatusSetter,
            ISubmissionResultSetter submissionResultSetter,
            ISubmissionFilesProvider filesProvider,
            IReservedDevicesProvider reservedDevicesProvider,
            ISubmissionProcessingLogger logger,
            IApkReader apkReader)
            : base(submissionResultSetter)
        {
            _adbClientProvider = adbClientProvider;
            _gradleRunner = gradleRunner;
            _instrumentationsOutputParser = instrumentationsOutputParser;
            _temporaryFolderProvider = temporaryFolderProvider;
            _submissionStatusSetter = submissionStatusSetter;
            _filesProvider = filesProvider;
            _reservedDevicesProvider = reservedDevicesProvider;
            _logger = logger;
            _apkReader = apkReader;
        }

        private async Task ExtractTemplateFilesAsync(ITemporaryFolder temporaryFolder)
        {
            var downloadFileStream = await _filesProvider.GetFileAsync("template");

            await using var downloadedFile = new MemoryStream();
            await downloadFileStream.CopyToAsync(downloadedFile);

            using (var mutableZipArchive = new ZipArchive(downloadedFile, ZipArchiveMode.Update, leaveOpen: true))
            {
                var levelsToReduce = mutableZipArchive
                    .Entries
                    .Where(e => e.Length != 0)
                    .Min(e => e.FullName.Count(c => c == '/'));

                if (levelsToReduce > 0)
                {
                    var entriesToMove = mutableZipArchive.Entries.ToList();
                    foreach (var entryToMove in entriesToMove)
                    {
                        var newEntryPath = string.Join('/', entryToMove.FullName.Split('/').Skip(levelsToReduce));
                        if (newEntryPath == string.Empty)
                            continue;

                        var movedEntry = mutableZipArchive.CreateEntry(newEntryPath);

                        await using (var entryToMoveStream = entryToMove.Open())
                        await using (var movedEntryStream = movedEntry.Open())
                            await entryToMoveStream.CopyToAsync(movedEntryStream);

                        entryToMove.Delete();
                    }
                }
            }

            downloadedFile.Seek(offset: 0, SeekOrigin.Begin);

            using var zipArchive = new ZipArchive(downloadedFile);

            zipArchive.ExtractToDirectory(temporaryFolder.AbsolutePath, overwriteFiles: true);

            _logger.LogInformation("Extracted template files into the directory: {temporaryFolder}", temporaryFolder);
        }

        private async Task ExtractSubmitFilesAsync(ITemporaryFolder temporaryFolder)
        {
            await using var downloadFileStream = await _filesProvider.GetFileAsync("submission");

            await using var downloadedFile = new MemoryStream();
            await downloadFileStream.CopyToAsync(downloadedFile);

            using (var mutableZipArchive = new ZipArchive(downloadedFile, ZipArchiveMode.Update, leaveOpen: true))
            {
                var levelsToReduce = mutableZipArchive
                    .Entries
                    .Where(e => e.Length != 0)
                    .Min(e => e.FullName.Count(c => c == '/'));

                if (levelsToReduce > 0)
                {
                    var entriesToMove = mutableZipArchive.Entries.ToList();
                    foreach (var entryToMove in entriesToMove)
                    {
                        var newEntryPath = string.Join('/', entryToMove.FullName.Split('/').Skip(levelsToReduce));
                        if (newEntryPath == string.Empty)
                            continue;

                        var movedEntry = mutableZipArchive.CreateEntry(newEntryPath);

                        await using (var entryToMoveStream = entryToMove.Open())
                        await using (var movedEntryStream = movedEntry.Open())
                            await entryToMoveStream.CopyToAsync(movedEntryStream);

                        entryToMove.Delete();
                    }
                }
            }

            downloadedFile.Seek(offset: 0, SeekOrigin.Begin);

            using var zipArchive = new ZipArchive(downloadedFile);

            zipArchive.ExtractToDirectory(temporaryFolder.AbsolutePath, overwriteFiles: true);

            _logger.LogInformation("Extracted submit files into the directory: {temporaryFolder}", temporaryFolder);
        }

        protected override async Task<object> CheckSubmissionCoreAsync(SubmissionProcessingContext processingContext)
        {

            await _submissionStatusSetter.SetStatusAsync(new ProcessingStatus("checking_started"));

            using var temporaryFolder = _temporaryFolderProvider.Get();

            _logger.LogInformation("Generated temporary directory: {temporaryFolder}", temporaryFolder);

            await _submissionStatusSetter.SetStatusAsync(new ProcessingStatus("unzip_files"));
            using (_logger.BeginScope(new Dictionary<string, string> { { "extractingFile", "submit" } }))
            {
                try
                {
                    await ExtractSubmitFilesAsync(temporaryFolder);
                }
                catch (ZipException e)
                {
                    _logger.LogError(e, "Cannot extract submitted file.");
                    return new ValidationErrorResult(ValidationError: "Cannot extract submitted file.");
                }
                catch (InvalidDataException e)
                {
                    _logger.LogError(e, "Cannot extract submitted file.");
                    return new ValidationErrorResult(ValidationError: "Cannot extract submitted file.");
                }
                catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogError(e, "can't find files for submission");

                    return new ValidationErrorResult(
                        ValidationError: "Internal check error: can't find files for submission.");
                }
            }

            await ExtractTemplateFilesAsync(temporaryFolder);

            if (!_gradleRunner.IsGradlewInstalledInDirectory(temporaryFolder.AbsolutePath))
                return new ValidationErrorResult(
                    ValidationError: "Can't find Gradlew launcher. Please, check template and submission files.");

            await _submissionStatusSetter.SetStatusAsync(new ProcessingStatus("validate_submission"));

            var submissionProjects = await _gradleRunner.ExecuteTaskAsync(
                tempDirectory: temporaryFolder.AbsolutePath,
                taskName: "projects",
                cancellationToken: processingContext.CancellationToken);

            if (!submissionProjects.IsSuccessful)
                return new ValidationErrorResult(ValidationError: $"Can't get project list of submission: {Environment.NewLine}{Environment.NewLine}StdErr: {Environment.NewLine}{submissionProjects.StandardError}{Environment.NewLine}{Environment.NewLine}StdOut: {Environment.NewLine}{submissionProjects.StandardOutput}");

            if (submissionProjects.StandardOutput.Split(Environment.NewLine).Count(l => l.Contains("Project")) > 1)
                return new ValidationErrorResult(ValidationError: "Submission must have only one project.");

            if (!submissionProjects.StandardOutput.Contains("Project ':app'"))
                return new ValidationErrorResult(ValidationError: "Submission must have project with the name 'app'.");

            await _submissionStatusSetter.SetStatusAsync(new ProcessingStatus("gradle_build"));

            var assembleDebugTaskResult = await _gradleRunner.ExecuteTaskAsync(
                tempDirectory: temporaryFolder.AbsolutePath,
                taskName: "assembleDebug",
                processingContext.CancellationToken);
            if (!assembleDebugTaskResult.IsSuccessful)
                return new CompilationErrorResult(assembleDebugTaskResult);

            var assembleDebugAndroidTestResult = await _gradleRunner.ExecuteTaskAsync(
                tempDirectory: temporaryFolder.AbsolutePath,
                taskName: "assembleDebugAndroidTest",
                processingContext.CancellationToken);
            if (!assembleDebugAndroidTestResult.IsSuccessful)
                return new CompilationErrorResult(assembleDebugAndroidTestResult);

            await _submissionStatusSetter.SetStatusAsync(new ProcessingStatus("install_application"));

            var tests =
                Enumerable
                    .Range(0, SimultaneousTestsCount)
                    .Select(_ => TestApplication(processingContext, temporaryFolder));

            var testResults = await Task.WhenAll(tests);
            return testResults.OrderByDescending(testResult => testResult.Grade).First();
        }

        private async Task<CheckResult> TestApplication(SubmissionProcessingContext processingContext,
                                                        ITemporaryFolder temporaryFolder)
        {
            var adbClient = _adbClientProvider.GetAdbClient();

            using var device = await _reservedDevicesProvider.ReserveDeviceAsync(processingContext.CancellationToken);
            var deviceData = device.DeviceData;

            var packageManager = new PackageManager(adbClient, deviceData);

            var baseApksPath = Path.Join(temporaryFolder.AbsolutePath, "app", "build", "outputs", "apk");

            var applicationApkFile = Path.Join(baseApksPath, "debug", "app-debug.apk");

            try
            {
                packageManager.UninstallPackage(await _apkReader.ReadPackageNameAsync(applicationApkFile));
            }
            catch (PackageInstallationException e)
            {
                _logger.LogError(e, "can't uninstall package");
            }

            packageManager.InstallPackage(applicationApkFile, reinstall: false);
            _logger.LogInformation("Install debug application in directory: {temporaryFolder}", temporaryFolder);

            var testingApkFile = Path.Join(baseApksPath, "androidTest", "debug", "app-debug-androidTest.apk");

            try
            {
                packageManager.UninstallPackage(await _apkReader.ReadPackageNameAsync(testingApkFile));
            }
            catch (PackageInstallationException e)
            {
                _logger.LogError(e, "can't uninstall package");
            }

            packageManager.InstallPackage(testingApkFile, reinstall: false);
            _logger.LogInformation("Install androidTest application in directory: {temporaryFolder}",
                temporaryFolder);

            await _submissionStatusSetter.SetStatusAsync(new ProcessingStatus("test"));

            var consoleOutputReceiver = new ConsoleOutputReceiver();

            _logger.LogInformation("Started testing of Android application");

            await adbClient.ExecuteRemoteCommandAsync(
                $"am instrument -r -w {await _apkReader.ReadPackageNameAsync(testingApkFile)}",
                deviceData,
                consoleOutputReceiver, Encoding.UTF8, processingContext.CancellationToken);

            _logger.LogInformation("Completed testing of Android application");

            var consoleOutput = consoleOutputReceiver.ToString();

            var result = _instrumentationsOutputParser.Parse(consoleOutput);
            return result.GetResult<CheckResult>();
        }
    }
}