using System;
using System.IO;

using ACE.Common.Cryptography;

namespace ACE.Server.Network
{
    public class ClientPacketFragment : PacketFragment
    {
        public bool Unpack(BinaryReader payload)
        {
            Console.WriteLine("Unpacking packet");
            Header.Unpack(payload);
            Console.WriteLine("Unpacked packet");

            if (Header.Size - PacketFragmentHeader.HeaderSize < 0)
            {
                Console.WriteLine("Packet Fragment Header Size is too small");
                return false;
            }

            if (Header.Size > 464)
            {
                Console.WriteLine("Packet Fragment Header Size is too big");
                return false;
            }

            Console.WriteLine("Attempting to read from packet fragment starting at {0}", Header.Size - PacketFragmentHeader.HeaderSize);
            Data = payload.ReadBytes(Header.Size - PacketFragmentHeader.HeaderSize);
            Console.WriteLine("Read from fragment successfully");

            return true;
        }

        public uint CalculateHash32()
        {
            Span<byte> buffer = stackalloc byte[PacketFragmentHeader.HeaderSize];

            Header.Pack(buffer);

            uint fragmentChecksum = Hash32.Calculate(buffer, buffer.Length) + Hash32.Calculate(Data, Data.Length);

            return fragmentChecksum;
        }
    }
}
