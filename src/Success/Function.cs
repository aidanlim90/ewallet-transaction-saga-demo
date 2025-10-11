using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;

namespace Success;

public class Function
{

    /// <summary>
    /// The main entry point for the Lambda function. The main function is called once during the Lambda init phase. It
    /// initializes the .NET Lambda runtime client passing in the function handler to invoke for each Lambda event and
    /// the JSON serializer to use for converting Lambda JSON format to the .NET types.
    /// </summary>
    private static async Task Main()
    {
        Func<MyRequest, ILambdaContext, Task<string>> handler = FunctionHandler;
        await LambdaBootstrapBuilder.Create(handler, new SourceGeneratorLambdaJsonSerializer<LambdaFunctionJsonSerializerContext>())
            .Build()
            .RunAsync();
    }

    public static Task<string> FunctionHandler(MyRequest input, ILambdaContext context)
    {
        return Task.FromResult($" status: {input.IsSufficient}, message: {input.ResponseCode}");

        // Return uppercase version of input
        // return Task.FromResult(input.ToUpperInvariant());
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(MyRequest))]
public partial class LambdaFunctionJsonSerializerContext : JsonSerializerContext
{
    // By using this partial class derived from JsonSerializerContext, we can generate reflection free JSON Serializer code at compile time
    // which can deserialize our class and properties. However, we must attribute this class to tell it what types to generate serialization code for.
    // See https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-source-generation
}

public class MyRequest
{
    public bool IsSufficient { get; set; }
    public string ResponseCode { get; set; } = string.Empty;
}
