using System.IO;
using System.Threading;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

public class ug_connect_component : MonoBehaviour
{
    private class UgFrame
    {
        public int width;
        public int height;

        public byte[] data;
        public int dataLen;
    }

    [SerializeField]
    GameObject m_plane;

    Material m_material;
    Mesh m_mesh;
    Texture2D m_tex;

    FileStream m_fifo;
    BlockingCollection<UgFrame> frameQueue;

    Thread reader;
    CancellationTokenSource cancelTokSrc;

    // Start is called before the first frame update
    void Start()
    {
        m_material = m_plane.GetComponent<Renderer>().material;
        m_mesh = m_plane.GetComponent<Mesh>();
        m_tex = new Texture2D(128, 128, TextureFormat.RGBA32, false);
        m_material.mainTexture = m_tex;

        frameQueue = new BlockingCollection<UgFrame>(2);

        cancelTokSrc = new CancellationTokenSource();
        reader = new Thread(readerThread);
        reader.IsBackground = true;
        reader.Start();
    }

    void blockingRead(Stream s, byte[] buf, int size, CancellationToken tok){
        int read = 0;

        while(read < size && !tok.IsCancellationRequested){
            read += s.Read(buf, read, size - read);
        }
    }

    bool readFrame(UgFrame f, CancellationToken tok){
        byte[] header = new byte[128];

        blockingRead(m_fifo, header, 128, tok);

        f.width = System.BitConverter.ToInt32(header, 0);
        f.height = System.BitConverter.ToInt32(header, 4);
        f.dataLen = System.BitConverter.ToInt32(header, 8);
        f.data = new byte[f.dataLen];

        blockingRead(m_fifo, f.data, f.dataLen, tok);

        return true;
    }

    void readerThread(){

        CancellationToken token = cancelTokSrc.Token;

        m_fifo = File.OpenRead("/tmp/fifo");

        while(!token.IsCancellationRequested){
            UgFrame f = new UgFrame();

            readFrame(f, token);

            frameQueue.Add(f);
        }
    }

    void UpdateTexture(UgFrame f){
        float aspect = (float) f.width / f.height;

        var texScale = aspect > 1 ? new Vector3(1, 1, 1 / aspect) : new Vector3(aspect, 1, 1);
        m_plane.transform.localScale = texScale;

        if(m_tex.width != f.width || m_tex.height != f.height)
            m_tex.Resize(f.width, f.height, TextureFormat.RGBA32, false);

        m_tex.LoadRawTextureData(f.data);
        m_tex.Apply();
    }

    // Update is called once per frame
    void Update()
    {
        UgFrame f;
        if(!frameQueue.TryTake(out f))
            return;

        UpdateTexture(f);
    }

    void OnApplicationQuit()
    {
        cancelTokSrc.Cancel();
        reader.Join();
        cancelTokSrc.Dispose();
    }
}
