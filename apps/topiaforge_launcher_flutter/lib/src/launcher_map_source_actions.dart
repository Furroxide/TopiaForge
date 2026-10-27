part of 'launcher_bloc.dart';

extension LauncherMapSourceActions on LauncherBloc {
  Future<List<PublishedMapSource>> _loadSavedMaps() async {
    if (_repository case final PublishedMapSourceRepository sources) {
      return sources.loadPublishedMapSources();
    }
    return const [];
  }

  Future<void> _onMapSourceSaved(
    PublishedMapSourceSaved event,
    Emitter<LauncherState> emit,
  ) async {
    await _guard(
      emit,
      'Saved map. Changes appear in Free Play after the next game start.',
      () async {
        if (_repository case final PublishedMapSourceRepository sources) {
          final updated = await sources.savePublishedMapSource(event.source);
          emit(state.copyWith(publishedMapSources: updated));
        } else {
          throw StateError(
            'Saving map sources is unavailable in this launcher.',
          );
        }
      },
    );
  }

  Future<void> _onMapSourceRemoved(
    PublishedMapSourceRemoved event,
    Emitter<LauncherState> emit,
  ) async {
    await _guard(
      emit,
      'Removed the saved reference. Changes apply after the next game start.',
      () async {
        if (_repository case final PublishedMapSourceRepository sources) {
          final updated = await sources.removePublishedMapSource(
            event.map,
            event.scene,
          );
          emit(state.copyWith(publishedMapSources: updated));
        } else {
          throw StateError(
            'Saving map sources is unavailable in this launcher.',
          );
        }
      },
    );
  }
}
