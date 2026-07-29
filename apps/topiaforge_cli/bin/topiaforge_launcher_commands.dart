part of 'topiaforge.dart';

extension _TopiaForgeLauncherCommands on _TopiaForgeCli {
  Future<int> _launcher(List<String> args) async {
    final operation = args.firstOrNull;
    final plan = _option(args, '--plan');
    if (plan == null || plan.trim().isEmpty) {
      throw UsageError(
        'Usage: topiaforge launcher apply-update|recover-update '
        '--plan <transaction-plan>',
      );
    }
    final helper = const LauncherUpdateTransactionHelper();
    switch (operation) {
      case 'apply-update':
        await helper.apply(plan);
        break;
      case 'recover-update':
        await helper.recover(plan);
        break;
      default:
        throw UsageError(
          'Usage: topiaforge launcher apply-update|recover-update '
          '--plan <transaction-plan>',
        );
    }
    return 0;
  }
}
