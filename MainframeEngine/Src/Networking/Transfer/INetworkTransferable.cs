namespace MainframeEngine.Networking;

public interface INetworkTransferable
{
    void NetworkWrite(NetBufferWriter writer);
    void NetworkRead(NetBufferReader reader);
}
