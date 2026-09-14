using System;

namespace PakReader.Parsers.Objects
{
    public readonly struct FBodyInstance : IUStruct
    {
        internal FBodyInstance(PackageReader reader)
        {
            throw new NotImplementedException(string.Format(FModel.Res.ParsingNotSupported, "FBodyInstance"));
        }
    }
}
