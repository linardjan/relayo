using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Relayo.Core.Data;

/// <summary>
/// Сравнение строк в SQL Server по умолчанию не учитывает регистр. Для ключей и идентификаторов
/// (ключ виджета, clientId сайта, id сообщения отправителя) это неверно: «Ab1» и «aB1» — разные значения.
/// Такие колонки получают двоичное сравнение.
/// </summary>
public static class SqlCollations
{
    public const string CaseSensitive = "Latin1_General_100_BIN2";

    public const string CaseInsensitive = "Latin1_General_100_CI_AS";

    public static PropertyBuilder<T> IsCaseSensitive<T>(this PropertyBuilder<T> property) =>
        property.UseCollation(CaseSensitive);
}
