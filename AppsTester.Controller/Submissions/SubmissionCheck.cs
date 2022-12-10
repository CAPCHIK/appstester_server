using System;

namespace AppsTester.Controller.Submissions
{
    public class SubmissionCheck
    {
        public Guid Id { get; set; }

        public int AttemptId { get; set; }
        
        public int AttemptStepId { get; set; }

        public string SerializedRequest { get; set; }

        public string LastSerializedStatus { get; set; }

        public int LastStatusVersion { get; set; }

        public string SerializedResult { get; set; }

        public DateTime SendingDateTimeUtc { get; set; } = DateTime.UtcNow;
    }
}