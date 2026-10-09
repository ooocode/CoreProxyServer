using Microsoft.EntityFrameworkCore;

namespace SignalrServerWebApp.Models
{
    public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
    {
        public DbSet<Device> Devices { get; set; }
    }
}
