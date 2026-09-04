using System.Collections.Generic;

namespace TidyMind
{
    public class Entity
    {
        // Közös mezők
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

        // Car / Motorcycle
        public string Model { get; set; }
        public string Year { get; set; }
        public string LicensePlate { get; set; }
        public string Mileage { get; set; }
        public string FuelType { get; set; }
        public string InsuranceExpiry { get; set; }
        public string TechExpiry { get; set; }

        // Book
        public string Author { get; set; }
        public string Publisher { get; set; }
        public string ISBN { get; set; }
        public string Genre { get; set; }
        public string Language { get; set; }
        public string Condition { get; set; }

        // Game / Movie
        public string Platform { get; set; }
        public string Director { get; set; }
        public string Format { get; set; }

        // Electronics
        public string DeviceType { get; set; }
        public string SerialNumber { get; set; }
        public string PurchaseDate { get; set; }
        public string WarrantyExpiry { get; set; }

        // Pet
        public string Species { get; set; }
        public string Breed { get; set; }
        public string BirthDate { get; set; }
        public string Gender { get; set; }
        public string ChipNumber { get; set; }
        public string VaccineExpiry { get; set; }
        public string Vet { get; set; }

        // Plant
        public string Location { get; set; }
        public string WateringFrequency { get; set; }
        public string LastRepotted { get; set; }

        // Property
        public string Address { get; set; }
        public string PropertyType { get; set; }
        public string Area { get; set; }
        public string Rooms { get; set; }
        public string RentPrice { get; set; }

        // Medicine
        public string MedicineType { get; set; }
        public string Manufacturer { get; set; }
        public string Dosage { get; set; }
        public string ExpiryDate { get; set; }

        // Artwork
        public string Artist { get; set; }
        public string ArtworkType { get; set; }
        public string ArtMaterial { get; set; }
        public string Size { get; set; }

        // Custom
        public List<CustomField> CustomFields { get; set; } = new List<CustomField>();
    }
}