﻿using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AppsTester.Checker.Android.Adb;
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
    internal class AndroidApplicationTester : ISubmissionChecker
    {
        private readonly IAdbClientProvider _adbClientProvider;
        private readonly IGradleRunner _gradleRunner;
        private readonly IInstrumentationsOutputParser _instrumentationsOutputParser;
        private readonly ISubmissionProcessingLogger _logger;
        private readonly ITemporaryFolderProvider _temporaryFolderProvider;
        private readonly IReservedDevicesProvider _reservedDevicesProvider;

        private readonly ISubmissionFilesProvider _filesProvider;
        private readonly ISubmissionPlainParametersProvider _plainParametersProvider;
        private readonly ISubmissionResultSetter _resultSetter;
        private readonly ISubmissionStatusSetter _statusSetter;

        public AndroidApplicationTester(IAdbClientProvider adbClientProvider,
            IGradleRunner gradleRunner,
            IInstrumentationsOutputParser instrumentationsOutputParser,
            ITemporaryFolderProvider temporaryFolderProvider, ISubmissionStatusSetter statusSetter, ISubmissionResultSetter resultSetter, ISubmissionPlainParametersProvider plainParametersProvider, ISubmissionFilesProvider filesProvider, IReservedDevicesProvider reservedDevicesProvider, ISubmissionProcessingLogger logger)
        {
            _adbClientProvider = adbClientProvider;
            _gradleRunner = gradleRunner;
            _instrumentationsOutputParser = instrumentationsOutputParser;
            _temporaryFolderProvider = temporaryFolderProvider;
            _statusSetter = statusSetter;
            _resultSetter = resultSetter;
            _plainParametersProvider = plainParametersProvider;
            _filesProvider = filesProvider;
            _reservedDevicesProvider = reservedDevicesProvider;
            _logger = logger;
        }

        private async Task CompilationResultAsync(GradleTaskExecutionResult taskExecutionResult, CancellationToken cancellationToken)
        {
            var totalErrorStringBuilder = new StringBuilder();
            totalErrorStringBuilder.AppendLine(taskExecutionResult.StandardOutput);
            totalErrorStringBuilder.AppendLine();
            totalErrorStringBuilder.AppendLine(taskExecutionResult.StandardError);

            await _resultSetter.SetResultAsync(new CompilationErrorResult(totalErrorStringBuilder.ToString().Trim()), cancellationToken);
        }

        private async Task ValidationResultAsync(string validationErrorMessage, CancellationToken cancellationToken)
        {
            await _resultSetter.SetResultAsync(new ValidationErrorResult(validationErrorMessage), cancellationToken);
        }

        private async Task ExtractTemplateFilesAsync(ITemporaryFolder temporaryFolder)
        {
            var fileStream = await _filesProvider.GetFileAsync("template");
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
            await Task.Run(() => archive.ExtractToDirectory(temporaryFolder.AbsolutePath, true));
            _logger.LogInformation($"Extracted template files into the directory: {temporaryFolder}");
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

            _logger.LogInformation($"Extracted submit files into the directory: {temporaryFolder}");
        }

        public async Task CheckSubmissionAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_plainParametersProvider.GetParameter<string>("android_package_name")))
            {
                await ValidationResultAsync(
                    "Invalid Android Package Name. Please, check parameter's value in question settings.",
                    cancellationToken);
                return;
            }

            await _statusSetter.SetStatusAsync(new ProcessingStatus("checking_started"), cancellationToken);

            using var temporaryFolder = _temporaryFolderProvider.Get();

            _logger.LogInformation($"Generated temporary directory: {temporaryFolder.AbsolutePath}");

            await _statusSetter.SetStatusAsync(new ProcessingStatus("unzip_files"), cancellationToken);

            try
            {
                await ExtractSubmitFilesAsync(temporaryFolder);
            }
            catch (ZipException e)
            {
                Console.WriteLine(e);

                await ValidationResultAsync(
                    "Cannot extract submitted file.",
                    cancellationToken);
                return;
            }

            await ExtractTemplateFilesAsync(temporaryFolder);

            await _statusSetter.SetStatusAsync(new ProcessingStatus("gradle_build"), cancellationToken);

            if (!_gradleRunner.IsGradlewInstalledInDirectory(temporaryFolder.AbsolutePath))
            {
                await ValidationResultAsync(
                    "Can't find Gradlew launcher. Please, check template and submission files.",
                    cancellationToken);
                return;
            }

            var assembleDebugTaskResult = await _gradleRunner.ExecuteTaskAsync(
                tempDirectory: temporaryFolder.AbsolutePath, taskName: "assembleDebug", cancellationToken);
            if (!assembleDebugTaskResult.IsSuccessful)
            {
                await CompilationResultAsync(assembleDebugTaskResult, cancellationToken);
                return;
            }

            var assembleDebugAndroidTestResult =
                await _gradleRunner.ExecuteTaskAsync(tempDirectory: temporaryFolder.AbsolutePath,
                    taskName: "assembleDebugAndroidTest", cancellationToken);
            if (!assembleDebugAndroidTestResult.IsSuccessful)
            {
                await CompilationResultAsync(assembleDebugTaskResult, cancellationToken);
                return;
            }

            await _statusSetter.SetStatusAsync(new ProcessingStatus("install_application"), cancellationToken);

            var adbClient = _adbClientProvider.GetAdbClient();

            using var device = await _reservedDevicesProvider.ReserveDeviceAsync(cancellationToken);
            var deviceData = device.DeviceData;

            var packageManager = new PackageManager(adbClient, deviceData);

            foreach (var package in packageManager.Packages.Where(p => p.Key.Contains("profexam")))
                packageManager.UninstallPackage(package.Key);

            var apkFilePath = Path.Join(temporaryFolder.AbsolutePath, "app", "build", "outputs", "apk", "debug",
                "app-debug.apk");
            packageManager.InstallPackage(apkFilePath, true);
            _logger.LogInformation($"Reinstalled debug application in directory: {temporaryFolder.AbsolutePath}");

            var apkFilePath2 = Path.Join(temporaryFolder.AbsolutePath, "app", "build", "outputs", "apk", "androidTest",
                "debug",
                "app-debug-androidTest.apk");
            packageManager.InstallPackage(apkFilePath2, true);
            _logger.LogInformation($"Reinstalled androidTest application in directory: {temporaryFolder.AbsolutePath}");

            await _statusSetter.SetStatusAsync(new ProcessingStatus("test"), cancellationToken);

            var consoleOutputReceiver = new ConsoleOutputReceiver();

            _logger.LogInformation("Started testing of Android application");

            await adbClient.ExecuteRemoteCommandAsync(
                $"am instrument -r -w {_plainParametersProvider.GetParameter<string>("android_package_name")}",
                deviceData,
                consoleOutputReceiver, Encoding.UTF8, cancellationToken);

            _logger.LogInformation("Completed testing of Android application");

            var consoleOutput = consoleOutputReceiver.ToString();

            var result = _instrumentationsOutputParser.Parse(consoleOutput);
            await _resultSetter.SetResultAsync(result.GetResult<AndroidCheckResult>(), cancellationToken);
        }
    }
}