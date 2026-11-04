using System.Buffers.Binary;
using System.Net;

namespace OneBlindMouse.Core;

/// <summary>
/// The small part of STUN (RFC 5389) needed to learn this client's public address: a binding request, and parsing of the binding response.
/// A public STUN server replies with the address and port it saw the request come from, which is the address a NAT gives this socket.
/// </summary>
public static class Stun
{
	/// <summary>Length of a STUN message header, and of a binding request (which has no attributes).</summary>
	public const int HeaderLength = 20;

	/// <summary>Length of a transaction id.</summary>
	public const int TransactionIdLength = 12;

	/// <summary>Fixed value in every STUN header. It also masks the address in XOR-MAPPED-ADDRESS.</summary>
	private const uint MagicCookie = 0x2112A442;

	/// <summary>Message types and attribute types used here.</summary>
	private const ushort BindingRequest = 0x0001, BindingSuccess = 0x0101;
	private const ushort AttrMappedAddress = 0x0001, AttrXorMappedAddress = 0x0020;

	/// <summary>Address family codes in address attributes.</summary>
	private const byte FamilyIPv4 = 0x01, FamilyIPv6 = 0x02;

	/// <summary>Encode a binding request with transaction id <paramref name="transaction"/>.</summary>
	public static byte[] Request(ReadOnlySpan<byte> transaction)
	{
		// Header only: type, zero attribute length, magic cookie, transaction id
		var msg = new byte[HeaderLength];
		BinaryPrimitives.WriteUInt16BigEndian(msg, BindingRequest);
		BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), MagicCookie);
		transaction.CopyTo(msg.AsSpan(8));
		return msg;
	}

	/// <summary>True if <paramref name="data"/> looks like a STUN message. STUN messages start with two zero bits and contain the magic cookie.</summary>
	public static bool IsStun(ReadOnlySpan<byte> data)
	{
		return data.Length >= HeaderLength && (data[0] & 0xC0) == 0 && BinaryPrimitives.ReadUInt32BigEndian(data[4..]) == MagicCookie;
	}

	/// <summary>
	/// Parse a binding success response. Returns its transaction id and the mapped address, preferring XOR-MAPPED-ADDRESS.
	/// Returns false for any other message, a malformed message, or a response without an address.
	/// </summary>
	public static bool TryParseResponse(ReadOnlySpan<byte> data, out byte[] transaction, out IPEndPoint mapped)
	{
		// The header must describe exactly the bytes received
		transaction = [];
		mapped = null!;
		if (!IsStun(data) || BinaryPrimitives.ReadUInt16BigEndian(data) != BindingSuccess || BinaryPrimitives.ReadUInt16BigEndian(data[2..]) != data.Length - HeaderLength)
			return false;

		// Walk the attributes. Each is type, length, value, padded to a multiple of four bytes.
		var header = data[..HeaderLength];
		IPEndPoint? plain = null, xored = null;
		for (var attrs = data[HeaderLength..]; attrs.Length >= 4;)
		{
			// Stop at a truncated attribute
			var type = BinaryPrimitives.ReadUInt16BigEndian(attrs);
			var length = BinaryPrimitives.ReadUInt16BigEndian(attrs[2..]);
			var padded = (length + 3) & ~3;
			if (4 + padded > attrs.Length)
				return false;

			// Decode the two address attributes; ignore the rest
			var value = attrs.Slice(4, length);
			switch (type)
			{
				case AttrXorMappedAddress:
				{
					xored = ReadAddress(value, header, xor: true) ?? xored;
					break;
				}
				case AttrMappedAddress:
				{
					plain = ReadAddress(value, header, xor: false) ?? plain;
					break;
				}
				default:
				{
					break;
				}
			}
			attrs = attrs[(4 + padded)..];
		}

		// Older servers only send MAPPED-ADDRESS
		if ((xored ?? plain) is not { } address)
			return false;

		transaction = data.Slice(8, TransactionIdLength).ToArray();
		mapped = address;
		return true;
	}

	/// <summary>Decode an address attribute value. XOR addresses are masked with the magic cookie (and the transaction id for IPv6).</summary>
	private static IPEndPoint? ReadAddress(ReadOnlySpan<byte> value, ReadOnlySpan<byte> header, bool xor)
	{
		// Layout: reserved, family, port, address
		if (value.Length < 4)
			return null;

		var family = value[1];
		var size = family switch
		{
			FamilyIPv4 => 4,
			FamilyIPv6 => 16,
			_ => 0,
		};
		if (size == 0 || value.Length != 4 + size)
			return null;

		// The mask is the cookie followed by the transaction id, which is exactly header bytes 4..20
		var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
		var bytes = value.Slice(4, size).ToArray();
		if (xor)
		{
			port ^= (ushort)(MagicCookie >> 16);
			for (var i = 0; i != size; ++i)
				bytes[i] ^= header[4 + i];
		}
		return new IPEndPoint(new IPAddress(bytes), port);
	}
}
