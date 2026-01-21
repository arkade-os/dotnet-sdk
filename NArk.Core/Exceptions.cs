namespace NArk.Core;

public class AlreadyLockedVtxoException(string msg) : Exception(msg);

public class UnableToSignUnknownContracts(string msg) : Exception(msg);

public class AdditionalInformationRequiredException(string msg) : Exception(msg);