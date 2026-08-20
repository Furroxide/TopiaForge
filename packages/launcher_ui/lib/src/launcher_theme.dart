import 'package:flutter/material.dart';

class TopiaForgeBrandAssets {
  static const package = 'launcher_ui';

  static const logo = 'assets/brand/topiaforge-wordmark.png';
  static const icon = 'assets/brand/topiaforge-icon.png';
  static const cityHeader = 'assets/brand/topiaforge-city-header.webp';
  static const babyStitch = 'assets/brand/baby-stitch.webp';
  static const sheriff = 'assets/brand/sheriff.webp';
}

class TopiaForgeBrandFonts {
  static const body = 'TopiaForgeQuicksand';
  static const display = 'TopiaForgeAudiowide';
}

class TopiaForgePalette {
  static const paper = Color(0xFFF5F1E8);
  static const paperWarm = Color(0xFFFFF7E9);
  static const surface = Color(0xFFFFFCF6);
  static const surfaceAlt = Color(0xFFFFF3E4);
  static const surfaceTint = Color(0xFFFFE0BE);
  static const border = Color(0xFFE4B373);
  static const borderStrong = Color(0xFFFF7A11);
  static const text = Color(0xFF2D3748);
  static const mutedText = Color(0xFF6C6670);
  static const faintText = Color(0xFF928A7C);
  static const launch = Color(0xFFFF7A11);
  static const launchDark = Color(0xFFCC620E);
  static const accent = Color(0xFF20F6FE);
  static const accentDark = Color(0xFF168E96);
  static const magenta = Color(0xFFFF6B9D);
  static const magentaDark = Color(0xFFB9446C);
  static const discord = Color(0xFF5865F2);
  static const discordDark = Color(0xFF3B4399);
  static const good = Color(0xFF148D63);
  static const warning = Color(0xFFD68017);
  static const danger = Color(0xFFC83E4D);
  static const darkPanel = Color(0xFF2D3748);
  static const logPanel = Color(0xFF1F2530);
  static const white = Color(0xFFFFFFFF);
}

ThemeData buildTopiaForgeTheme() {
  final colorScheme =
      ColorScheme.fromSeed(
        seedColor: TopiaForgePalette.launch,
        brightness: Brightness.light,
        surface: TopiaForgePalette.surface,
      ).copyWith(
        primary: TopiaForgePalette.launch,
        onPrimary: TopiaForgePalette.white,
        secondary: TopiaForgePalette.accentDark,
        onSecondary: TopiaForgePalette.white,
        tertiary: TopiaForgePalette.magenta,
        error: TopiaForgePalette.danger,
        onError: TopiaForgePalette.white,
        surface: TopiaForgePalette.surface,
        onSurface: TopiaForgePalette.text,
        surfaceContainerHighest: TopiaForgePalette.surfaceAlt,
        outline: TopiaForgePalette.border,
        outlineVariant: TopiaForgePalette.surfaceTint,
      );

  return ThemeData(
    useMaterial3: true,
    splashFactory: InkRipple.splashFactory,
    brightness: Brightness.light,
    colorScheme: colorScheme,
    fontFamily: TopiaForgeBrandFonts.body,
    package: TopiaForgeBrandAssets.package,
    scaffoldBackgroundColor: TopiaForgePalette.paper,
    canvasColor: TopiaForgePalette.paper,
    textSelectionTheme: const TextSelectionThemeData(
      cursorColor: TopiaForgePalette.launch,
      selectionColor: Color(0x5520F6FE),
      selectionHandleColor: TopiaForgePalette.launch,
    ),
    textTheme: TextTheme(
      headlineSmall: _displayStyle(
        fontSize: 26,
        color: TopiaForgePalette.text,
        height: 1.05,
      ),
      titleLarge: _displayStyle(
        fontSize: 24,
        color: TopiaForgePalette.text,
        height: 1.05,
      ),
      titleMedium: const TextStyle(
        fontSize: 15,
        fontWeight: FontWeight.w800,
        color: TopiaForgePalette.text,
        height: 1.25,
      ),
      titleSmall: const TextStyle(
        fontSize: 13,
        fontWeight: FontWeight.w800,
        color: TopiaForgePalette.text,
        height: 1.25,
      ),
      labelLarge: const TextStyle(
        fontSize: 13,
        fontWeight: FontWeight.w800,
        color: TopiaForgePalette.text,
      ),
      bodyMedium: const TextStyle(
        fontSize: 13,
        fontWeight: FontWeight.w600,
        color: TopiaForgePalette.text,
        height: 1.35,
      ),
      bodySmall: const TextStyle(
        fontSize: 12,
        fontWeight: FontWeight.w600,
        color: TopiaForgePalette.mutedText,
        height: 1.35,
      ),
    ),
    iconTheme: const IconThemeData(color: TopiaForgePalette.text),
    dividerTheme: const DividerThemeData(
      color: TopiaForgePalette.surfaceTint,
      thickness: 1,
      space: 1,
    ),
    inputDecorationTheme: _inputDecorationTheme(),
    filledButtonTheme: FilledButtonThemeData(style: _filledButtonStyle()),
    outlinedButtonTheme: OutlinedButtonThemeData(style: _outlinedButtonStyle()),
    textButtonTheme: TextButtonThemeData(style: _textButtonStyle()),
    iconButtonTheme: IconButtonThemeData(style: _iconButtonStyle()),
    navigationRailTheme: const NavigationRailThemeData(
      backgroundColor: Color(0xEEFFF7E9),
      indicatorColor: TopiaForgePalette.surfaceTint,
      selectedIconTheme: IconThemeData(color: TopiaForgePalette.launch),
      unselectedIconTheme: IconThemeData(color: TopiaForgePalette.mutedText),
      selectedLabelTextStyle: TextStyle(
        color: TopiaForgePalette.text,
        fontSize: 12,
        fontWeight: FontWeight.w800,
      ),
      unselectedLabelTextStyle: TextStyle(
        color: TopiaForgePalette.mutedText,
        fontSize: 12,
        fontWeight: FontWeight.w700,
      ),
    ),
    listTileTheme: const ListTileThemeData(
      dense: true,
      selectedTileColor: Color(0xFFFFE8D1),
      contentPadding: EdgeInsets.symmetric(horizontal: 12, vertical: 3),
      iconColor: TopiaForgePalette.mutedText,
      selectedColor: TopiaForgePalette.launchDark,
      textColor: TopiaForgePalette.text,
    ),
    switchTheme: SwitchThemeData(
      thumbColor: WidgetStateProperty.resolveWith((states) {
        return states.contains(WidgetState.selected)
            ? TopiaForgePalette.white
            : TopiaForgePalette.faintText;
      }),
      trackColor: WidgetStateProperty.resolveWith((states) {
        return states.contains(WidgetState.selected)
            ? TopiaForgePalette.launch
            : TopiaForgePalette.surfaceTint;
      }),
    ),
    dropdownMenuTheme: const DropdownMenuThemeData(
      textStyle: TextStyle(
        color: TopiaForgePalette.text,
        fontWeight: FontWeight.w700,
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: TopiaForgePalette.surface,
      ),
    ),
    tooltipTheme: TooltipThemeData(
      decoration: BoxDecoration(
        color: TopiaForgePalette.darkPanel,
        borderRadius: BorderRadius.circular(14),
        border: Border.all(color: TopiaForgePalette.launch, width: 2),
        boxShadow: _smallShadow,
      ),
      textStyle: const TextStyle(
        color: TopiaForgePalette.white,
        fontSize: 12,
        fontWeight: FontWeight.w700,
      ),
    ),
    dialogTheme: DialogThemeData(
      backgroundColor: TopiaForgePalette.surface,
      surfaceTintColor: Colors.transparent,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(28),
        side: const BorderSide(color: TopiaForgePalette.launch, width: 3),
      ),
      titleTextStyle: _displayStyle(
        fontSize: 22,
        color: TopiaForgePalette.text,
      ),
      contentTextStyle: const TextStyle(
        color: TopiaForgePalette.text,
        fontSize: 14,
        fontWeight: FontWeight.w600,
        height: 1.35,
      ),
    ),
    progressIndicatorTheme: const ProgressIndicatorThemeData(
      color: TopiaForgePalette.launch,
      linearTrackColor: TopiaForgePalette.surfaceTint,
    ),
    cardTheme: CardThemeData(
      color: TopiaForgePalette.surface,
      surfaceTintColor: Colors.transparent,
      elevation: 0,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(26),
        side: const BorderSide(color: TopiaForgePalette.borderStrong, width: 2),
      ),
    ),
  );
}

ThemeData buildTopiaForgeHighContrastTheme() {
  final base = buildTopiaForgeTheme();
  return base.copyWith(
    scaffoldBackgroundColor: TopiaForgePalette.white,
    canvasColor: TopiaForgePalette.white,
    focusColor: TopiaForgePalette.accentDark,
    hoverColor: const Color(0x332D3748),
    colorScheme: base.colorScheme.copyWith(
      primary: TopiaForgePalette.darkPanel,
      onPrimary: TopiaForgePalette.white,
      secondary: TopiaForgePalette.accentDark,
      onSecondary: TopiaForgePalette.white,
      surface: TopiaForgePalette.white,
      onSurface: TopiaForgePalette.text,
      outline: TopiaForgePalette.darkPanel,
      outlineVariant: TopiaForgePalette.mutedText,
    ),
    dividerTheme: const DividerThemeData(
      color: TopiaForgePalette.darkPanel,
      thickness: 2,
      space: 2,
    ),
    navigationRailTheme: base.navigationRailTheme.copyWith(
      indicatorColor: TopiaForgePalette.white,
      selectedIconTheme: const IconThemeData(
        color: TopiaForgePalette.darkPanel,
      ),
      unselectedIconTheme: const IconThemeData(
        color: TopiaForgePalette.darkPanel,
      ),
    ),
  );
}

TextStyle _displayStyle({
  required double fontSize,
  required Color color,
  double height = 1.1,
}) {
  return TextStyle(
    fontFamily: TopiaForgeBrandFonts.display,
    package: TopiaForgeBrandAssets.package,
    fontSize: fontSize,
    color: color,
    height: height,
  );
}

InputDecorationTheme _inputDecorationTheme() {
  const borderSide = BorderSide(color: TopiaForgePalette.border, width: 2);
  const focusedBorderSide = BorderSide(
    color: TopiaForgePalette.accentDark,
    width: 2.5,
  );
  return InputDecorationTheme(
    filled: true,
    fillColor: TopiaForgePalette.surface,
    labelStyle: const TextStyle(
      color: TopiaForgePalette.mutedText,
      fontWeight: FontWeight.w700,
    ),
    prefixIconColor: TopiaForgePalette.launch,
    suffixIconColor: TopiaForgePalette.launch,
    border: OutlineInputBorder(
      borderRadius: BorderRadius.circular(18),
      borderSide: borderSide,
    ),
    enabledBorder: OutlineInputBorder(
      borderRadius: BorderRadius.circular(18),
      borderSide: borderSide,
    ),
    focusedBorder: OutlineInputBorder(
      borderRadius: BorderRadius.circular(18),
      borderSide: focusedBorderSide,
    ),
    disabledBorder: OutlineInputBorder(
      borderRadius: BorderRadius.circular(18),
      borderSide: const BorderSide(color: TopiaForgePalette.surfaceTint),
    ),
    isDense: true,
    contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
  );
}

ButtonStyle _filledButtonStyle() {
  return ButtonStyle(
    minimumSize: const WidgetStatePropertyAll(Size(0, 40)),
    padding: const WidgetStatePropertyAll(
      EdgeInsets.symmetric(horizontal: 18, vertical: 12),
    ),
    backgroundColor: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.disabled)) {
        return TopiaForgePalette.surfaceTint;
      }
      return TopiaForgePalette.launch;
    }),
    foregroundColor: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.disabled)) {
        return TopiaForgePalette.faintText;
      }
      return TopiaForgePalette.white;
    }),
    iconColor: const WidgetStatePropertyAll(TopiaForgePalette.white),
    overlayColor: const WidgetStatePropertyAll(Color(0x22FFFFFF)),
    textStyle: const WidgetStatePropertyAll(
      TextStyle(fontSize: 13, fontWeight: FontWeight.w900),
    ),
    shape: WidgetStatePropertyAll(
      RoundedRectangleBorder(borderRadius: BorderRadius.circular(20)),
    ),
  );
}

ButtonStyle _outlinedButtonStyle() {
  return ButtonStyle(
    minimumSize: const WidgetStatePropertyAll(Size(0, 38)),
    padding: const WidgetStatePropertyAll(
      EdgeInsets.symmetric(horizontal: 15, vertical: 10),
    ),
    backgroundColor: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.disabled)) {
        return TopiaForgePalette.paperWarm;
      }
      return TopiaForgePalette.surface;
    }),
    foregroundColor: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.disabled)) {
        return TopiaForgePalette.faintText;
      }
      return TopiaForgePalette.text;
    }),
    iconColor: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.disabled)) {
        return TopiaForgePalette.faintText;
      }
      return TopiaForgePalette.launch;
    }),
    side: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.disabled)) {
        return const BorderSide(color: TopiaForgePalette.surfaceTint);
      }
      return const BorderSide(color: TopiaForgePalette.borderStrong, width: 2);
    }),
    textStyle: const WidgetStatePropertyAll(
      TextStyle(fontSize: 13, fontWeight: FontWeight.w900),
    ),
    shape: WidgetStatePropertyAll(
      RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
    ),
  );
}

ButtonStyle _textButtonStyle() {
  return ButtonStyle(
    foregroundColor: const WidgetStatePropertyAll(TopiaForgePalette.launchDark),
    textStyle: const WidgetStatePropertyAll(
      TextStyle(fontSize: 13, fontWeight: FontWeight.w900),
    ),
    shape: WidgetStatePropertyAll(
      RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
    ),
  );
}

ButtonStyle _iconButtonStyle() {
  return ButtonStyle(
    foregroundColor: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.disabled)) {
        return TopiaForgePalette.faintText;
      }
      return TopiaForgePalette.launch;
    }),
    backgroundColor: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.hovered)) {
        return TopiaForgePalette.surfaceTint;
      }
      return Colors.transparent;
    }),
    shape: WidgetStatePropertyAll(
      RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
    ),
  );
}

const _smallShadow = [
  BoxShadow(color: Color(0x33000000), offset: Offset(-3, 4), blurRadius: 0),
];
