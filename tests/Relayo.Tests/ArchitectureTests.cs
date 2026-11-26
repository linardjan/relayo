using System.Text.Json;

namespace Relayo.Tests;

/// <summary>
/// Публичный сервис доступен из интернета, поэтому у него не должно быть доступа к БД (п. 2.2 ТЗ).
/// Проверяем по полному дереву зависимостей сборки, включая транзитивные.
/// </summary>
public class ArchitectureTests
{
    [Theory]
    [InlineData("Relayo.Core")]
    [InlineData("Microsoft.Data.SqlClient")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("WolverineFx.SqlServer")]
    [InlineData("WolverineFx.EntityFrameworkCore")]
    public void Public_service_does_not_depend_on_database(string forbidden)
    {
        var libraries = PublicServiceLibraries();

        Assert.DoesNotContain(libraries, name =>
            name.Equals(forbidden, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(forbidden + ".", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dependency_list_is_actually_read()
    {
        // Защита от ложного успеха: если файл не прочитался, тесты выше прошли бы всегда.
        Assert.Contains("Relayo.Contracts", PublicServiceLibraries());
    }

    private static List<string> PublicServiceLibraries()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Relayo.Public.deps.json");
        Assert.True(File.Exists(path), $"{path} not found");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(p => p.Name.Split('/')[0])
            .ToList();
    }
}
