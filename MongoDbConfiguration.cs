using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;

namespace MLCategoryClassifier;

public static class MongoDbConfiguration
{
    private static bool _isConfigured = false;

    public static void Configure()
    {
        if (_isConfigured)
            return;

        // Configure camelCase convention for field names
        var conventionPack = new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new EnumRepresentationConvention(BsonType.String)
        };

        ConventionRegistry.Register("CamelCaseConvention", conventionPack, t => true);

        _isConfigured = true;
    }
}
