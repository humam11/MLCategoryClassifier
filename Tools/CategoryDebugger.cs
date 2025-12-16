using MongoDB.Bson;
using MongoDB.Driver;

namespace MLCategoryClassifier.Tools;

// Debugging tool to inspect and export training data for categories
// Usage: dotnet run -- debug <categoryId>
// Output: Creates #tools/<categoryId>.md with all training data details
//example:
//cd MLCategoryClassifier
// dotnet run -- debug 504


public static class CategoryDebugger
{
    public static async Task RunAsync(string[] args, IConfiguration configuration)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: dotnet run -- debug <categoryId>");
            Console.WriteLine("Example: dotnet run -- debug 504");
            Console.WriteLine("Output: Creates #tools/<categoryId>.md file");
            return;
        }

        if (!ushort.TryParse(args[1], out var categoryId))
        {
            Console.WriteLine($"Invalid category ID: {args[1]}");
            return;
        }

        var connectionString = configuration.GetConnectionString("MongoDB");
        var databaseName = configuration.GetValue<string>("MongoDB:DatabaseName") ?? "ClassifiedAdsDb";

        var client = new MongoClient(connectionString);
        var database = client.GetDatabase(databaseName);
        var collection = database.GetCollection<BsonDocument>("trainingdata");

        var filter = new BsonDocument("categoryId", categoryId);
        var bsonDoc = await collection.Find(filter).FirstOrDefaultAsync();

        if (bsonDoc == null)
        {
            Console.WriteLine($"Category {categoryId} not found in MongoDB");
            return;
        }

        // Build markdown content
        var md = new System.Text.StringBuilder();
        md.AppendLine($"# Category {categoryId} Debug Report");
        md.AppendLine();
        md.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        md.AppendLine();

        md.AppendLine("## Basic Info");
        md.AppendLine();
        md.AppendLine($"- **_id**: {bsonDoc.GetValue("_id", "")}");
        md.AppendLine($"- **categoryId**: {bsonDoc.GetValue("categoryId", 0)}");
        md.AppendLine($"- **nameArabic**: {bsonDoc.GetValue("nameArabic", "")}");
        md.AppendLine($"- **nameKurdish**: {bsonDoc.GetValue("nameKurdish", "")}");
        md.AppendLine($"- **urlSlugArabic**: {bsonDoc.GetValue("urlSlugArabic", "")}");
        md.AppendLine($"- **urlSlugKurdish**: {bsonDoc.GetValue("urlSlugKurdish", "")}");
        md.AppendLine($"- **isLeaf**: {bsonDoc.GetValue("isLeaf", false)}");
        md.AppendLine($"- **hasModels**: {bsonDoc.GetValue("hasModels", false)}");
        md.AppendLine();

        // Arabic Training Examples
        md.AppendLine("## Arabic Training Examples");
        md.AppendLine();
        if (bsonDoc.Contains("arabicTrainingExamples") && bsonDoc["arabicTrainingExamples"].IsBsonArray)
        {
            var examples = bsonDoc["arabicTrainingExamples"].AsBsonArray;
            md.AppendLine($"Count: {examples.Count}");
            md.AppendLine();
            if (examples.Count > 0)
            {
                foreach (var ex in examples)
                    md.AppendLine($"- {ex}");
            }
            else
            {
                md.AppendLine("*(none)*");
            }
        }
        else
        {
            md.AppendLine("*(field not found)*");
        }
        md.AppendLine();

        // Kurdish Training Examples
        md.AppendLine("## Kurdish Training Examples");
        md.AppendLine();
        if (bsonDoc.Contains("kurdishTrainingExamples") && bsonDoc["kurdishTrainingExamples"].IsBsonArray)
        {
            var examples = bsonDoc["kurdishTrainingExamples"].AsBsonArray;
            md.AppendLine($"Count: {examples.Count}");
            md.AppendLine();
            if (examples.Count > 0)
            {
                foreach (var ex in examples)
                    md.AppendLine($"- {ex}");
            }
            else
            {
                md.AppendLine("*(none)*");
            }
        }
        else
        {
            md.AppendLine("*(field not found)*");
        }
        md.AppendLine();

        // Brands/Models
        md.AppendLine("## Brands/Models");
        md.AppendLine();
        if (bsonDoc.Contains("brandsModels") && bsonDoc["brandsModels"].IsBsonArray)
        {
            var brands = bsonDoc["brandsModels"].AsBsonArray;
            md.AppendLine($"Count: {brands.Count}");
            md.AppendLine();
            if (brands.Count > 0)
            {
                md.AppendLine("| ID | English | Arabic | Kurdish |");
                md.AppendLine("|---|---|---|---|");
                foreach (var bm in brands)
                {
                    if (bm.IsBsonDocument)
                    {
                        var bmDoc = bm.AsBsonDocument;
                        md.AppendLine($"| {bmDoc.GetValue("brandModelId", 0)} | {bmDoc.GetValue("nameEnglish", "")} | {bmDoc.GetValue("nameArabic", "")} | {bmDoc.GetValue("nameKurdish", "")} |");
                    }
                }
            }
            else
            {
                md.AppendLine("*(none)*");
            }
        }
        else
        {
            md.AppendLine("*(field not found)*");
        }
        md.AppendLine();

        // Combined Training Data for ML (Arabic)
        md.AppendLine("## Combined Training Data for ML (Arabic)");
        md.AppendLine();
        var combinedAr = BuildCombinedTrainingData(bsonDoc, "ar");
        md.AppendLine($"Total items: {combinedAr.Count}");
        md.AppendLine();
        foreach (var item in combinedAr)
            md.AppendLine($"- {item}");
        md.AppendLine();

        // Combined Training Data for ML (Kurdish)
        md.AppendLine("## Combined Training Data for ML (Kurdish)");
        md.AppendLine();
        var combinedKr = BuildCombinedTrainingData(bsonDoc, "kr");
        md.AppendLine($"Total items: {combinedKr.Count}");
        md.AppendLine();
        foreach (var item in combinedKr)
            md.AppendLine($"- {item}");
        md.AppendLine();

        // Raw JSON
        md.AppendLine("## Raw JSON");
        md.AppendLine();
        md.AppendLine("```json");
        md.AppendLine(bsonDoc.ToString());
        md.AppendLine("```");

        // Save to MLCategoryClassifier/Tools folder
        var toolsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Tools");
        toolsDir = Path.GetFullPath(toolsDir);
        Directory.CreateDirectory(toolsDir);
        var filePath = Path.Combine(toolsDir, $"{categoryId}.md");
        await File.WriteAllTextAsync(filePath, md.ToString());

        Console.WriteLine($"Debug report created: {filePath}");
    }

    private static List<string> BuildCombinedTrainingData(BsonDocument bsonDoc, string langCode)
    {
        var combined = new List<string>();
        var nameField = langCode == "ar" ? "nameArabic" : "nameKurdish";
        var examplesField = langCode == "ar" ? "arabicTrainingExamples" : "kurdishTrainingExamples";

        // Add category name
        var categoryName = bsonDoc.GetValue(nameField, "").ToString();
        if (!string.IsNullOrWhiteSpace(categoryName))
            combined.Add($"[Category Name] {categoryName}");

        // Add training examples
        if (bsonDoc.Contains(examplesField) && bsonDoc[examplesField].IsBsonArray)
        {
            foreach (var ex in bsonDoc[examplesField].AsBsonArray)
                combined.Add($"[Training Example] {ex}");
        }

        // Add brand/model names if hasModels
        var hasModels = bsonDoc.GetValue("hasModels", false).AsBoolean;
        if (hasModels && bsonDoc.Contains("brandsModels") && bsonDoc["brandsModels"].IsBsonArray)
        {
            foreach (var bm in bsonDoc["brandsModels"].AsBsonArray)
            {
                if (bm.IsBsonDocument)
                {
                    var bmDoc = bm.AsBsonDocument;
                    var nameEn = bmDoc.GetValue("nameEnglish", "").ToString();
                    if (!string.IsNullOrWhiteSpace(nameEn))
                        combined.Add($"[Brand/Model EN] {nameEn}");
                    var localName = bmDoc.GetValue(nameField, "").ToString();
                    if (!string.IsNullOrWhiteSpace(localName))
                        combined.Add($"[Brand/Model] {localName}");
                }
            }
        }

        return combined;
    }
}
