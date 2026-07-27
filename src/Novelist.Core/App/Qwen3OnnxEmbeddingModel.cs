namespace Novelist.Core.App;

public static class Qwen3OnnxEmbeddingModel
{
    public const string ModelId = "Qwen/Qwen3-Embedding-0.6B";
    public const string DisplayName = "Qwen3 Embedding 0.6B FP16";
    public const string ModelDirectoryName = "Qwen3-Embedding-0.6B-fp16";
    public const string ModelFileName = "model.onnx";
    public const string TokenizerFileName = "tokenizer.json";
    public const int Dimensions = 1024;
    public const int MaxSequenceLength = 4096;
    public const int MicroBatchSize = 1;
    public const long PadTokenId = 151643;
    public const bool NormalizeEmbeddings = true;
    public const string PoolingStrategy = "last-token";
    public const string TokenizerKind = "hugging-face-json";
    public const string ExecutionProvider = "directml";
    public const string QueryInstruction =
        "Instruct: Given a Chinese web novel query, retrieve relevant passages that answer the query\nQuery:";
}
