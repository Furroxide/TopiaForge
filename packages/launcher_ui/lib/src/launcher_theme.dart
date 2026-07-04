import 'package:flutter/material.dart';

class QuantumWorksBrandAssets {
  static const package = 'launcher_ui';

  static const logo = 'assets/brand/quantumworks-logo.svg';
  static const cityHeader = 'assets/brand/quantumworks-city-header.webp';
  static const babyStitch = 'assets/brand/baby-stitch.webp';
  static const robot = 'assets/brand/robot.webp';
  static const sheriff = 'assets/brand/sheriff.webp';
}

class QuantumWorksBrandFonts {
  static const body = 'QuantumWorksQuicksand';
  static const display = 'QuantumWorksAudiowide';
}

class QuantumWorksPalette {
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

ThemeData buildQuantumWorksTheme() {
  final colorScheme =
      ColorScheme.fromSeed(
        seedColor: QuantumWorksPalette.launch,
        brightness: Brightness.light,
        surface: QuantumWorksPalette.surface,
      ).copyWith(
        primary: QuantumWorksPalette.launch,
        onPrimary: QuantumWorksPalette.white,
        secondary: QuantumWorksPalette.accentDark,
        onSecondary: QuantumWorksPalette.white,
        tertiary: QuantumWorksPalette.magenta,
        error: QuantumWorksPalette.danger,
        onError: QuantumWorksPalette.white,
        surface: QuantumWorksPalette.surface,
        onSurface: QuantumWorksPalette.text,
        surfaceContainerHighest: QuantumWorksPalette.surfaceAlt,
        outline: QuantumWorksPalette.border,
        outlineVariant: QuantumWorksPalette.surfaceTint,
      );

  return ThemeData(
    useMaterial3: true,
    brightness: Brightness.light,
    colorScheme: colorScheme,
    fontFamily: QuantumWorksBrandFonts.body,
    package: QuantumWorksBrandAssets.package,
    scaffoldBackgroundColor: QuantumWorksPalette.paper,
    canvasColor: QuantumWorksPalette.paper,
    textSelectionTheme: const TextSelectionThemeData(
      cursorColor: QuantumWorksPalette.launch,
      selectionColor: Color(0x5520F6FE),
      selectionHandleColor: QuantumWorksPalette.launch,
    ),
    textTheme: TextTheme(
      headlineSmall: _displayStyle(
        fontSize: 26,
        color: QuantumWorksPalette.text,
        height: 1.05,
      ),
      titleLarge: _displayStyle(
        fontSize: 24,
        color: QuantumWorksPalette.text,
        height: 1.05,
      ),
      titleMedium: const TextStyle(
        fontSize: 15,
        fontWeight: FontWeight.w800,
        color: QuantumWorksPalette.text,
        height: 1.25,
      ),
      titleSmall: const TextStyle(
        fontSize: 13,
        fontWeight: FontWeight.w800,
        color: QuantumWorksPalette.text,
        height: 1.25,
      ),
      labelLarge: const TextStyle(
        fontSize: 13,
        fontWeight: FontWeight.w800,
        color: QuantumWorksPalette.text,
      ),
      bodyMedium: const TextStyle(
        fontSize: 13,
        fontWeight: FontWeight.w600,
        color: QuantumWorksPalette.text,
        height: 1.35,
      ),
      bodySmall: const TextStyle(
        fontSize: 12,
        fontWeight: FontWeight.w600,
        color: QuantumWorksPalette.mutedText,
        height: 1.35,
      ),
    ),
    iconTheme: const IconThemeData(color: QuantumWorksPalette.text),
    dividerTheme: const DividerThemeData(
      color: QuantumWorksPalette.surfaceTint,
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
      indicatorColor: QuantumWorksPalette.surfaceTint,
      selectedIconTheme: IconThemeData(color: QuantumWorksPalette.launch),
      unselectedIconTheme: IconThemeData(color: QuantumWorksPalette.mutedText),
      selectedLabelTextStyle: TextStyle(
        color: QuantumWorksPalette.text,
        fontSize: 12,
        fontWeight: FontWeight.w800,
      ),
      unselectedLabelTextStyle: TextStyle(
        color: QuantumWorksPalette.mutedText,
        fontSize: 12,
        fontWeight: FontWeight.w700,
      ),
    ),
    listTileTheme: const ListTileThemeData(
      dense: true,
      selectedTileColor: Color(0xFFFFE8D1),
      contentPadding: EdgeInsets.symmetric(horizontal: 12, vertical: 3),
      iconColor: QuantumWorksPalette.mutedText,
      selectedColor: QuantumWorksPalette.launchDark,
      textColor: QuantumWorksPalette.text,
    ),
    switchTheme: SwitchThemeData(
      thumbColor: WidgetStateProperty.resolveWith((states) {
        return states.contains(WidgetState.selected)
            ? QuantumWorksPalette.white
            : QuantumWorksPalette.faintText;
      }),
      trackColor: WidgetStateProperty.resolveWith((states) {
        return states.contains(WidgetState.selected)
            ? QuantumWorksPalette.launch
            : QuantumWorksPalette.surfaceTint;
      }),
    ),
    dropdownMenuTheme: const DropdownMenuThemeData(
      textStyle: TextStyle(
        color: QuantumWorksPalette.text,
        fontWeight: FontWeight.w700,
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: QuantumWorksPalette.surface,
      ),
    ),
    tooltipTheme: TooltipThemeData(
      decoration: BoxDecoration(
        color: QuantumWorksPalette.darkPanel,
        borderRadius: BorderRadius.circular(14),
        border: Border.all(color: QuantumWorksPalette.launch, width: 2),
        boxShadow: _smallShadow,
      ),
      textStyle: const TextStyle(
        color: QuantumWorksPalette.white,
        fontSize: 12,
        fontWeight: FontWeight.w700,
      ),
    ),
    dialogTheme: DialogThemeData(
      backgroundColor: QuantumWorksPalette.surface,
      surfaceTintColor: Colors.transparent,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(28),
        side: const BorderSide(color: QuantumWorksPalette.launch, width: 3),
      ),
      titleTextStyle: _displayStyle(fontSize: 22, color: QuantumWorksPalette.text),
      contentTextStyle: const TextStyle(
        color: QuantumWorksPalette.text,
        fontSize: 14,
        fontWeight: FontWeight.w600,
        height: 1.35,
      ),
    ),
    progressIndicatorTheme: const ProgressIndicatorThemeData(
      color: QuantumWorksPalette.launch,
      linearTrackColor: QuantumWorksPalette.surfaceTint,
    ),
    cardTheme: CardThemeData(
      color: QuantumWorksPalette.surface,
      surfaceTintColor: Colors.transparent,
      elevation: 0,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(26),
        side: const BorderSide(color: QuantumWorksPalette.borderStrong, width: 2),
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
    fontFamily: QuantumWorksBrandFonts.display,
    package: QuantumWorksBrandAssets.package,
    fontSize: fontSize,
    color: color,
    height: height,
  );
}

InputDecorationTheme _inputDecorationTheme() {
  const borderSide = BorderSide(color: QuantumWorksPalette.border, width: 2);
  const focusedBorderSide = BorderSide(
    color: QuantumWorksPalette.accentDark,
    width: 2.5,
  );
  return InputDecorationTheme(
    filled: true,
    fillColor: QuantumWorksPalette.surface,
    labelStyle: const TextStyle(
      color: QuantumWorksPalette.mutedText,
      fontWeight: FontWeight.w700,
    ),
    prefixIconColor: QuantumWorksPalette.launch,
    suffixIconColor: QuantumWorksPalette.launch,
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
      borderSide: const BorderSide(color: QuantumWorksPalette.surfaceTint),
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
        return QuantumWorksPalette.surfaceTint;
      }
      return QuantumWorksPalette.launch;
    }),
    foregroundColor: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.disabled)) {
        return QuantumWorksPalette.faintText;
      }
      return QuantumWorksPalette.white;
    }),
    iconColor: const WidgetStatePropertyAll(QuantumWorksPalette.white),
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
        return QuantumWorksPalette.paperWarm;
      }
      return QuantumWorksPalette.surface;
    }),
    foregroundColor: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.disabled)) {
        return QuantumWorksPalette.faintText;
      }
      return QuantumWorksPalette.text;
    }),
    iconColor: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.disabled)) {
        return QuantumWorksPalette.faintText;
      }
      return QuantumWorksPalette.launch;
    }),
    side: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.disabled)) {
        return const BorderSide(color: QuantumWorksPalette.surfaceTint);
      }
      return const BorderSide(color: QuantumWorksPalette.borderStrong, width: 2);
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
    foregroundColor: const WidgetStatePropertyAll(QuantumWorksPalette.launchDark),
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
        return QuantumWorksPalette.faintText;
      }
      return QuantumWorksPalette.launch;
    }),
    backgroundColor: WidgetStateProperty.resolveWith((states) {
      if (states.contains(WidgetState.hovered)) {
        return QuantumWorksPalette.surfaceTint;
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
