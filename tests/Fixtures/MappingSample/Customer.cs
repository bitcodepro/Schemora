using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Schemora.Fixtures;

[Table("Customers", Schema = "dbo")]
public sealed class Customer
{
    [Key] public int CustomerId { get; set; }
    [Column("ExternalId")] public string? ExternalReference { get; set; }
}
