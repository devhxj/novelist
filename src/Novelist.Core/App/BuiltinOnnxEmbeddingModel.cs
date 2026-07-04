namespace Novelist.Core.App;

public static class BuiltinOnnxEmbeddingModel
{
    public const string ProviderKey = "onnx";
    public const string ProviderType = "onnx";
    public const string ModelId = "bge-small-zh-v1.5";
    public const string DisplayName = "BGE Small ZH v1.5 int8";
    public const int Dimensions = 512;
    public const int MaxSequenceLength = 512;
    public const bool NormalizeEmbeddings = true;
    public const string PoolingStrategy = "cls";
    public const string QueryInputKind = "query";
    public const string DocumentInputKind = "document";
    public const string QueryInstruction = "为这个句子生成表示以用于检索相关文章：";
}
