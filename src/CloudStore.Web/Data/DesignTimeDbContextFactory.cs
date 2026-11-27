//summary//
// Generates SQL Server migrations without starting MVC or requiring Azure connections.
//summary//
//-------------------Start Of File------------------//
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CloudStore.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // The offline placeholder cannot point at an Azure database. No connection is opened
        // by migration generation; actual database updates require explicit configuration.
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Server=invalid.example;Database=CloudStoreDesignOnly;Integrated Security=true;Encrypt=true;Connect Timeout=1";
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
    }
}
//-------------------End Of File------------------//
