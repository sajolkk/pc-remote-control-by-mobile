import 'dart:async';

import 'package:remote_control_app/data/input/input_sender.dart';
import 'package:remote_control_app/protocol/protocol.dart';

/// Stands in for the connection: records every command in the order it was written, and answers
/// when (and how) the test says.
class RecordingTransport {
  final sent = <(String, Map<String, dynamic>?)>[];
  final _waiting = <Completer<ResponseEnvelope?>>[];

  /// When false, responses are held back until [answer] or [answerAll] is called.
  bool autoAnswer = true;

  /// The error every automatic answer carries, or null for success.
  String? failWith;

  InputTransport get send => _send;

  Future<ResponseEnvelope?> _send(String command, {Map<String, dynamic>? args}) {
    sent.add((command, args));

    if (autoAnswer) return Future.value(_response(failWith));

    final completer = Completer<ResponseEnvelope?>();
    _waiting.add(completer);
    return completer.future;
  }

  /// Answers the oldest unanswered command.
  void answer({String? error}) => _waiting.removeAt(0).complete(_response(error));

  void answerAll({String? error}) {
    while (_waiting.isNotEmpty) {
      answer(error: error);
    }
  }

  int get unanswered => _waiting.length;

  List<String> get commands => [for (final (command, _) in sent) command];

  /// Each command as `name args`, for comparing whole exchanges at a glance.
  List<String> get log => [for (final (command, args) in sent) args == null ? command : '$command $args'];

  void clear() => sent.clear();

  static ResponseEnvelope _response(String? error) => ResponseEnvelope(
        id: 'x',
        ok: error == null,
        data: const <String, dynamic>{},
        error: error == null ? null : ProtocolError(code: error, message: 'refused: $error'),
      );
}
