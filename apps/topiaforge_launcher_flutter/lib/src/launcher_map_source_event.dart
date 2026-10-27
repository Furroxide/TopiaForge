part of 'launcher_event.dart';

class PublishedMapSourceSaved extends LauncherEvent {
  const PublishedMapSourceSaved(this.source);
  final PublishedMapSource source;
}

class PublishedMapSourceRemoved extends LauncherEvent {
  const PublishedMapSourceRemoved(this.map, this.scene);
  final String map, scene;
}
