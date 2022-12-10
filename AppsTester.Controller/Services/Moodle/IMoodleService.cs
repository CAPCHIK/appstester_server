using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AppsTester.Controller.Submissions;

namespace AppsTester.Controller.Services
{
    public interface IMoodleService
    {
        Task<Dictionary<int, int[]>> GetSubmissionsToCheckAsync(CancellationToken stoppingToken);
        Task<Submission> GetSubmissionAsync(int id, CancellationToken stoppingToken, string includedFileHashes = "");
        Task SetSubmissionStatusAsync(int id, string status, CancellationToken stoppingToken);
        Task SetSubmissionResultAsync(int id, string result, CancellationToken stoppingToken);
    }
}