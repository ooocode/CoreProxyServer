using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SignalrServerWebApp.Models
{
    public class Device
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        [Required]
        public required string Id { get; set; }

        [Required]
        public required string ServerId { get; set; }

        public required string UserName { get; set; }

        public required string OsVersion { get; set; }

        public string? UserAgent { get; set; }
        public string? IpAddress { get; set; }

        public required DateTimeOffset OnlineDateTime { get; set; }
    }
}
