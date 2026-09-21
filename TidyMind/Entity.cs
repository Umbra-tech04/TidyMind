using System.Collections.Generic;

namespace TidyMind
{
    public class Entity
    {
        public string EntityType { get; set; }
        public string Name { get; set; }
        public string ImagePath { get; set; }
        public string Notes { get; set; }

        // Clothing
        public string Brand { get; set; }
        public string Color { get; set; }
        public string Material { get; set; }
        public string Season { get; set; }
        public string PurchasePrice { get; set; }
        public List<SizeQuantity> Sizes { get; set; } = new List<SizeQuantity>();
    }
}
