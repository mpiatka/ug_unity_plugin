using System;
using System.IO;
using System.Threading;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using UnityEngine;

public class ug_connect_unix : MonoBehaviour
{
    private enum PixFmt{
        None = 0,
        RGBA = 1,
        UYVY = 2,
        RGB = 11
    }

    private class UgFrame
    {
        public int width;
        public int height;
        public PixFmt fmt;

        public byte[] data;
        public int dataLen;
    }


    [SerializeField]
    GameObject m_plane;

    Material m_material;
    Mesh m_mesh;
    Texture2D m_tex;

    BlockingCollection<UgFrame> frameQueue;

    Thread reader;
    CancellationTokenSource cancelTokSrc;

    // Start is called before the first frame update
    void Start()
    {
        m_material = m_plane.GetComponent<Renderer>().material;
        m_mesh = m_plane.GetComponent<Mesh>();
        m_tex = new Texture2D(128, 128, TextureFormat.RGB24, false);
        m_material.mainTexture = m_tex;

        frameQueue = new BlockingCollection<UgFrame>(2);

        cancelTokSrc = new CancellationTokenSource();
        reader = new Thread(readerThread);
        reader.IsBackground = true;
        reader.Start();
    }

    int blockingRead(Socket s, byte[] buf, int size, CancellationToken tok){
        int read = 0;

        while(read < size && !tok.IsCancellationRequested && s.Connected){
			try {
				int readThisTime = s.Receive(buf, read, size - read, SocketFlags.None);
				read += readThisTime;
				if(readThisTime == 0)
					break;
			} catch {
				return read;
			}
        }

		return read;
    }

    bool readFrame(Socket sock, UgFrame f, CancellationToken tok){
        byte[] header = new byte[128];

        if(blockingRead(sock, header, header.Length, tok) != header.Length)
			return false;

        f.width = System.BitConverter.ToInt32(header, 0);
        f.height = System.BitConverter.ToInt32(header, 4);
        f.dataLen = System.BitConverter.ToInt32(header, 8);
        f.fmt = (PixFmt) System.BitConverter.ToInt32(header, 12);
        f.data = new byte[f.dataLen];

		int read = blockingRead(sock, f.data, f.dataLen, tok);
	   
		return read == f.dataLen;
    }

	bool waitForConnection(Socket listenSock, CancellationToken tok){
		Debug.Log("Waiting for connection");
		while(!listenSock.Poll(500000, System.Net.Sockets.SelectMode.SelectRead)){
			if(tok.IsCancellationRequested)
				return false;
		}

		return true;
	}

    void readerThread(){

        CancellationToken token = cancelTokSrc.Token;

		var path = "/tmp/ug_unix";
		if (System.IO.File.Exists(path))
			System.IO.File.Delete(path);
		using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
		var endpoint = new UnixDomainSocketEndPoint(path);
		socket.Bind(endpoint);
		socket.Listen(5);

		while(!token.IsCancellationRequested){
			if(!waitForConnection(socket, token))
				break;
			var dataSock = socket.Accept();
			dataSock.ReceiveTimeout = 1000;

			clientLoop(dataSock, token);
		}

		socket.Close();
		socket.Dispose();

		if (System.IO.File.Exists(path))
			System.IO.File.Delete(path);
    }

	void clientLoop(Socket sock, CancellationToken token){
        while(!token.IsCancellationRequested && sock.Connected){
            UgFrame f = new UgFrame();
            if(!readFrame(sock, f, token))
				break;
			try{
				frameQueue.Add(f, token);
			} catch{

			}
        }

		if(token.IsCancellationRequested){
			Debug.Log("Cancellation requested, shutting down socket...");
			try{
				sock.Shutdown(SocketShutdown.Both);
			} catch{

			}
		}

		Debug.Log("client disconnected");
		sock.Close();
		sock.Dispose();
    }

    TextureFormat unityFmtFromPixFmt(PixFmt fmt){
        switch(fmt){
            case PixFmt.RGBA:
                return TextureFormat.RGBA32;
            case PixFmt.RGB:
                return TextureFormat.RGB24;
            default:
                throw new ArgumentException("Unsupported pix fmt " + fmt);
        }
    }

    void UpdateTexture(UgFrame f){
        float aspect = (float) f.width / f.height;

        var texScale = aspect > 1 ? new Vector3(-1, 1, 1 / aspect) : new Vector3(-aspect, 1, 1);
        m_plane.transform.localScale = texScale;

        var fmt = unityFmtFromPixFmt(f.fmt);

        if(m_tex.width != f.width || m_tex.height != f.height || m_tex.format != fmt)
            m_tex.Reinitialize(f.width, f.height, fmt, false);

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
		frameQueue.Dispose();
    }
}
