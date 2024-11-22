using System.Collections.Generic;
using MES_WebService.Server.Models;
using Microsoft.EntityFrameworkCore;


namespace MES_WebService.Server.Data
{
    public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
    {
        public DbSet<Log> Logs { get; set; }
    }
}