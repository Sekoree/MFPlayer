namespace S.Media.NDI.Config;

public enum NDIQueueOverflowPolicy
{
    DropOldest = 0,
    DropNewest = 1,
    RejectIncoming = 2,
}

