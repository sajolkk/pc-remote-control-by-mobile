/// Length-prefixed framing for the control channel, mirroring `RemoteAgent.Protocol/FrameCodec.cs`.
///
/// Layout: `[4-byte big-endian payload length][1-byte frame type][payload]`. The length covers
/// the payload only. TLS underneath provides confidentiality, integrity and ordering, so the
/// framing itself carries no checksum.
///
/// Used by the phone when it is the host. The viewer side keeps its own copy inside
/// `ControlClient`, written before this file existed; the two must agree, and the test for this
/// file checks them against each other.
library;

import 'dart:typed_data';

import 'protocol.dart';

/// One frame read from the wire.
class Frame {
  const Frame(this.type, this.payload);

  final FrameType type;
  final Uint8List payload;
}

/// Thrown when the stream cannot be a sequence of frames we would have written. The connection
/// must be dropped: there is no safe way to find the next frame boundary.
class MalformedFrameException implements Exception {
  const MalformedFrameException(this.message);

  final String message;

  @override
  String toString() => message;
}

/// Encodes frames, and decodes them from a stream of arbitrary byte chunks.
class FrameCodec {
  FrameCodec({this.maxPayloadBytes = defaultMaxPayloadBytes});

  /// Size of the fixed header: 4 length bytes plus 1 type byte.
  static const int headerBytes = 5;

  /// Largest payload accepted or emitted, 1 MiB, the same as the PC.
  static const int defaultMaxPayloadBytes = 1024 * 1024;

  final int maxPayloadBytes;

  final _buffer = BytesBuilder(copy: false);

  /// Builds one frame.
  Uint8List encode(FrameType type, List<int> payload) {
    if (payload.length > maxPayloadBytes) {
      throw ArgumentError.value(payload.length, 'payload', 'exceeds the frame size limit');
    }

    final frame = Uint8List(headerBytes + payload.length);
    frame[0] = (payload.length >> 24) & 0xFF;
    frame[1] = (payload.length >> 16) & 0xFF;
    frame[2] = (payload.length >> 8) & 0xFF;
    frame[3] = payload.length & 0xFF;
    frame[4] = type.value;
    frame.setRange(headerBytes, frame.length, payload);
    return frame;
  }

  /// Adds bytes as they arrive and returns every frame that is now complete, in order.
  ///
  /// Throws [MalformedFrameException] on a length we would never have written or an unknown
  /// frame type. The PC drops the connection in both cases, and so does the host.
  List<Frame> decode(List<int> chunk) {
    _buffer.add(chunk);
    final frames = <Frame>[];

    while (true) {
      final buffered = _buffer.toBytes();
      if (buffered.length < headerBytes) {
        _retain(buffered);
        return frames;
      }

      final length =
          (buffered[0] << 24) | (buffered[1] << 16) | (buffered[2] << 8) | buffered[3];

      if (length < 0 || length > maxPayloadBytes) {
        throw MalformedFrameException('Frame declared an invalid payload length of $length bytes.');
      }

      final type = FrameType.fromValue(buffered[4]);
      if (type == null) {
        throw MalformedFrameException(
          'Frame declared unknown type 0x${buffered[4].toRadixString(16).padLeft(2, '0')}.',
        );
      }

      final total = headerBytes + length;
      if (buffered.length < total) {
        _retain(buffered);
        return frames;
      }

      frames.add(Frame(type, Uint8List.sublistView(buffered, headerBytes, total)));
      _retain(Uint8List.sublistView(buffered, total));
    }
  }

  void _retain(Uint8List bytes) {
    _buffer
      ..clear()
      ..add(bytes);
  }
}
