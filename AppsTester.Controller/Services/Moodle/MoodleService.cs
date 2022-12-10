using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppsTester.Controller.Moodle;
using AppsTester.Controller.Submissions;

namespace AppsTester.Controller.Services
{
    internal class MoodleService : IMoodleService
    {
        private readonly IMoodleCommunicator _moodleCommunicator;

        public MoodleService(IMoodleCommunicator moodleCommunicator)
        {
            _moodleCommunicator = moodleCommunicator;
        }

        public async Task<Dictionary<int, int[]>> GetSubmissionsToCheckAsync(CancellationToken stoppingToken)
        {
            return await _moodleCommunicator.GetFunctionResultAsync<Dictionary<int, int[]>>(
                functionName: "local_qtype_get_submissions_to_check", cancellationToken: stoppingToken);
        }

        public async Task<Submission> GetSubmissionAsync(int id, CancellationToken stoppingToken, string includedFileHashes = "")
        {
            if (includedFileHashes == "")
            {
                return await _moodleCommunicator.GetFunctionResultAsync<Submission>(
                    functionName: "local_qtype_get_submission",
                    functionParams: new Dictionary<string, object>
                    {
                        ["id"] = id
                    },
                    cancellationToken: stoppingToken);
            }

            return await _moodleCommunicator.GetFunctionResultAsync<Submission>(
                functionName: "local_qtype_get_submission",
                functionParams: new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["included_file_hashes"] = includedFileHashes
                },
                cancellationToken: stoppingToken);
        }
        
        public async Task SetSubmissionStatusAsync(int id, string status, CancellationToken stoppingToken)
        {
            await _moodleCommunicator.CallFunctionAsync(
                functionName: "local_qtype_set_submission_status",
                functionParams: new Dictionary<string, object>
                {
                    ["id"] = id
                },
                requestParams: new Dictionary<string, string>
                {
                    ["status"] = status
                },
                cancellationToken: stoppingToken);
        }

        public async Task SetSubmissionResultAsync(int id, string result, CancellationToken stoppingToken)
        {
            await _moodleCommunicator.CallFunctionAsync(
                functionName: "local_qtype_set_submission_results",
                functionParams: new Dictionary<string, object>
                {
                    ["id"] = id
                },
                requestParams: new Dictionary<string, string>
                {
                    ["result"] = result
                },
                cancellationToken: stoppingToken);
        }
    }
}