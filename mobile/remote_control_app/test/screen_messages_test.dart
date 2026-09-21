import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/protocol/protocol.dart';
import 'package:remote_control_app/protocol/screen_messages.dart';

void main() {
  group('MediaQuality', () {
    test('parses the telemetry event exactly as the PC serializes it', () {
      // Field names copied from MediaQualityEvent in ScreenMessages.cs. If either side renames a
      // field, this is the test that says so.
      final json = jsonDecode('''
        {"sessionId":"ms-0123456789ab","state":"streaming","fps":58.5,"targetFps":60,
         "bitrateKbps":12400,"targetBitrateKbps":14000,"width":1920,"height":1080,
         "lossPercent":0.2,"rttMs":3.4,"pipelineMs":9.1,"codec":"H264",
         "encoder":"NVIDIA H.264 Encoder MFT","hardware":true,"level":0,"degradedLevel":5,
         "indicator":"good"}
      ''') as Map<String, dynamic>;

      final quality = MediaQuality.fromJson(json);

      expect(quality.sessionId, 'ms-0123456789ab');
      expect(quality.state, 'streaming');
      expect(quality.fps, 58.5);
      expect(quality.targetFps, 60);
      expect(quality.bitrateKbps, 12400);
      expect(quality.width, 1920);
      expect(quality.height, 1080);
      expect(quality.rttMs, 3.4);
      expect(quality.hardware, isTrue);
      expect(quality.encoder, 'NVIDIA H.264 Encoder MFT');
      expect(quality.isDegraded, isFalse);
      expect(quality.isPaused, isFalse);
    });

    test('recognises the degraded rung and the paused state', () {
      final degraded = MediaQuality.fromJson({'sessionId': 's', 'state': 'streaming', 'level': 5, 'degradedLevel': 5});
      expect(degraded.isDegraded, isTrue);

      final paused = MediaQuality.fromJson({'sessionId': 's', 'state': 'paused', 'reason': MediaPauseReasons.desktopUnavailable});
      expect(paused.isPaused, isTrue);
      expect(MediaPauseReasons.describe(paused.reason), contains('locked'));
    });

    test('a missing rtt stays null rather than reading as zero', () {
      // Zero RTT would be a lie on the overlay; "not measured yet" is the truth.
      final quality = MediaQuality.fromJson({'sessionId': 's', 'state': 'streaming'});

      expect(quality.rttMs, isNull);
      expect(quality.isDegraded, isFalse, reason: 'no degradedLevel means the ladder cannot degrade');
    });
  });

  group('MonitorList', () {
    test('parses monitors and the selected id', () {
      final list = MonitorList.fromJson({
        'monitors': [
          {'id': 'mon-a', 'name': 'Display 1 (1920×1080, primary)', 'width': 1920, 'height': 1080, 'refreshHz': 60, 'primary': true, 'scale': 1.25},
          {'id': 'mon-b', 'name': 'Display 2', 'width': 2560, 'height': 1440, 'x': -2560},
        ],
        'selectedId': 'mon-a',
      });

      expect(list.monitors, hasLength(2));
      expect(list.selectedId, 'mon-a');
      expect(list.monitors.first.primary, isTrue);
      expect(list.monitors.first.scale, 1.25);
      expect(list.monitors.first.resolution, '1920 × 1080 · 60 Hz');
      expect(list.monitors.last.x, -2560);
      expect(list.monitors.last.resolution, '2560 × 1440');
    });

    test('ignores malformed entries instead of failing the whole list', () {
      final list = MonitorList.fromJson({
        'monitors': ['nonsense', 42, {'id': 'mon-a', 'width': 800, 'height': 600}],
      });

      expect(list.monitors.single.id, 'mon-a');
      expect(list.selectedId, isNull);
    });
  });

  group('QualityPreset', () {
    test('auto asks for the PC maximum rather than sending nothing', () {
      // The PC reads a missing field as "keep the current ceiling"; an empty Auto would never undo
      // an earlier 720p choice.
      final json = QualityPreset.auto.toJson();

      expect(json['maxHeight'], greaterThanOrEqualTo(2160));
      expect(json['maxFps'], greaterThanOrEqualTo(120));
    });

    test('every preset has a distinct label', () {
      final labels = QualityPreset.all.map((p) => p.label).toList();
      expect(labels.toSet(), hasLength(labels.length));
    });
  });

  group('wire names', () {
    // The command and event names are a contract with the PC. The C# source is the authority;
    // when it is present (a full checkout), check every Phase 3 name against it.
    final csharp = File('../../windows/RemoteAgent.Protocol/CommandNames.cs');

    test('Phase 3 commands and events match the PC', () {
      if (!csharp.existsSync()) {
        markTestSkipped('PC sources not present in this checkout.');
        return;
      }

      final source = csharp.readAsStringSync();
      for (final name in [
        Commands.screenMonitorList,
        Commands.screenSelectMonitor,
        Commands.screenScreenshot,
        Commands.mediaOffer,
        Commands.mediaIce,
        Commands.mediaStop,
        Commands.mediaSetQuality,
        Events.mediaQuality,
        Events.monitorsChanged,
      ]) {
        expect(source, contains('"$name"'), reason: '$name is not declared on the PC');
      }
    });

    test('the busy error code matches the PC', () {
      final errors = File('../../windows/RemoteAgent.Protocol/ErrorCodes.cs');
      if (!errors.existsSync()) {
        markTestSkipped('PC sources not present in this checkout.');
        return;
      }

      expect(errors.readAsStringSync(), contains('"${ErrorCodes.busy}"'));
    });
  });
}
