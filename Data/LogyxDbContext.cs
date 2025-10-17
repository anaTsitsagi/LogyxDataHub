using Microsoft.EntityFrameworkCore;
using LogyxDataHub.Models;

namespace LogyxDataHub.Data
{
    public class LogyxDbContext : DbContext
    {
        public LogyxDbContext(DbContextOptions<LogyxDbContext> options) : base(options) { }

        public DbSet<HiroWiring> HiroWirings { get; set; } = null!;
    }
}