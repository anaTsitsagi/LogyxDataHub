using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LogyxDataHub.Models
{
    [Table("HIRO_WIRING", Schema = "dbo")]
    public class HiroWiring
    {
        [Key]
        public double? EntryNumber { get; set; } // keep as double because DB column is float

        public string? DocumentNumber { get; set; }
        public string? Debet { get; set; }
        public string? Credit { get; set; }

        [Column("debet_sub")]
        public string? DebetSub { get; set; }

        [Column("credit_sub")]
        public string? CreditSub { get; set; }

        // Mapped DB column (float) -> CLR double? to match DB
        public double? Amount { get; set; }

        // Application-facing decimal conversion for money math
        [NotMapped]
        public decimal? AmountDecimal
        {
            get
            {
                if (!Amount.HasValue) return null;
                // Convert using Convert.ToDecimal to reduce precision surprises
                return Convert.ToDecimal(Amount.Value);
            }
        }

        public string? Currency { get; set; }
        public string? Description { get; set; }
        public double? Quantity { get; set; }
        public string? Unit { get; set; }
        public string? PostedBy { get; set; }
        public DateTime? OperationDate { get; set; }
        public DateTime? PostingDate { get; set; }
    }
}